# Étape 3 — Logs OTel (`billing-service`)

> Branche : `step-3-logs`
> Durée estimée : 20 min
> État à l'arrivée : `billing-service` instrumenté pour les **traces** (step 1) et les **métriques** (step 2), tout sort sur la **console**. Les services Python et Go sont également instrumentés par l'animateur.

## Préparation : passer sur la branche `step-3-logs`

Tu arrives de l'exo 2 avec des modifications locales sur `step-2-metrics`. On les **écarte** pour récupérer l'état propre de `step-3-logs` (solution de référence de l'exo 2 + point de départ de l'exo 3).

```bash
# 1. Annule toutes les modifications locales sur les fichiers suivis
git reset --hard

# 2. Supprime les fichiers non suivis
git clean -fd

# 3. Bascule sur la branche de l'exo 3
git checkout step-3-logs
```

> ⚠️ Ces commandes **détruisent** ton travail local de l'exo 2. Si tu veux conserver ta solution, fais d'abord un `git stash` ou un commit sur une branche perso avant la réinitialisation.

## Ce que tu vas apprendre

- Brancher le pipeline de logs d'ASP.NET Core sur **OpenTelemetry** avec `builder.Logging.AddOpenTelemetry()`.
- Comprendre comment `ILogger<T>` devient un **LogRecord structuré** avec `trace_id`, `span_id` et `resource.service.name` automatiquement corrélés.
- Ajouter des **logs métier** dans `BillingService.cs` pour rendre le flux de checkout observable.

## ASP.NET Core vs Python : une seule approche

Dans le workshop Python (step 3), on distingue deux approches : la **corrélation seule** (un formatter enrichit les logs stdio avec `trace_id`/`span_id`) et le **bridge OTel complet** (les logs passent par un `LoggerProvider` et deviennent des `LogRecord` exportables en OTLP).

**ASP.NET Core n'a pas cette dualité.**

Le système de logging d'ASP.NET Core est déjà abstrait derrière `ILogger` / `ILoggerProvider`. Appeler `builder.Logging.AddOpenTelemetry()` enregistre un `ILoggerProvider` qui bridge **toujours** l'intégralité du pipeline vers le `LoggerProvider` OTel. Il n'y a pas de mode dégradé "corrélation seule" : chaque `logger.LogInformation(...)` produit un `LogRecord` complet, enrichi automatiquement avec le `TraceId`, le `SpanId` et les attributs `resource` issus du même `ConfigureResource()` que les traces et les métriques. La corrélation est gratuite et systématique.

Avantage concret : là où Python requiert l'installation d'un instrumenteur séparé (`opentelemetry-instrumentation-logging`) et un appel explicite à `LoggingInstrumentor().instrument(set_logging_format=True)`, .NET donne des `LogRecord` corrélés avec une seule configuration.

## Exercice

### 1. Aucun nouveau package NuGet

Le package `OpenTelemetry.Extensions.Hosting` (déjà présent depuis le step 1) inclut le support pour `builder.Logging.AddOpenTelemetry()`. Pas de nouvelle dépendance à ajouter dans [billing-service/billing-service.csproj](../../../src/billing-service/billing-service.csproj).

### 2. Ajouter le logging OTel dans Program.cs

Modifier [billing-service/Program.cs](../../../src/billing-service/Program.cs) — ajouter le `using` en tête de fichier et le bloc de configuration **avant** `var app = builder.Build()` :

```csharp
using OpenTelemetry.Logs;
```

Puis, à côté du bloc `AddOpenTelemetry()` existant :

```csharp
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.ParseStateValues = true;
    o.AddConsoleExporter();
});
```

Points d'attention :

- **`builder.Logging` vs `builder.Services`** : `ILogger` est géré par le pipeline de logging d'ASP.NET Core, indépendant de l'IoC OTel. On configure les deux séparément, mais ils partagent automatiquement le même `Resource` (via `ConfigureResource()` sur `AddOpenTelemetry()`). Le SDK OTel .NET synchronise les deux au démarrage du host.
- **`IncludeFormattedMessage = true`** : peuple le champ `body` du `LogRecord` avec le message final rendu (après interpolation des paramètres). Sans cette option, `body` est vide et seuls les `attributes` contiennent les valeurs.
- **`IncludeScopes = true`** : capture les scopes `ILogger` (créés avec `logger.BeginScope(...)`) en tant qu'attributs du `LogRecord`.
- **`ParseStateValues = true`** : extrait les paramètres structurés du message (`{UserId}`, `{ItemCount}`…) en attributs indexables plutôt qu'en texte brut. C'est ce qui permet à Loki/Grafana de filtrer sur `UserId="..."`.
- **`AddConsoleExporter()`** : même exporter que pour les traces et métriques — cohérence jusqu'au step 4 où on basculera sur OTLP.

### 3. Ajouter des logs métier dans BillingService.cs

#### 3a. Injecter `ILogger<BillingService>` dans le constructeur

Modifier [billing-service/BillingService.cs](../../../src/billing-service/BillingService.cs) — ajouter le paramètre `logger` au constructeur primaire :

```csharp
public class BillingService(
    BillingRepository repository,
    IHttpClientFactory httpClientFactory,
    string productsUrl,
    string paymentUrl,
    ILogger<BillingService> logger)
```

#### 3b. Mettre à jour l'enregistrement DI dans Program.cs

La signature du constructeur a changé : il faut résoudre `ILogger<BillingService.BillingService>` depuis le conteneur. Modifier [billing-service/Program.cs](../../../src/billing-service/Program.cs) :

```csharp
builder.Services.AddSingleton(sp => new BillingService.BillingService(
    sp.GetRequiredService<BillingRepository>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    productsUrl,
    paymentUrl,
    sp.GetRequiredService<ILogger<BillingService.BillingService>>()
));
```

> Le type générique est `ILogger<BillingService.BillingService>` (namespace + nom de classe) — le compilateur est strict sur ce point.

#### 3c. Ajouter les logs dans la méthode `Checkout()`

Modifier [billing-service/BillingService.cs](../../../src/billing-service/BillingService.cs) — enrichir la méthode `Checkout()` avec trois événements :

```csharp
public async Task<(CheckoutResponse? Result, object? Error, int StatusCode)> Checkout(string userId, CartItem[] cartItems)
{
    logger.LogInformation("checkout_started {UserId} {ItemCount}", userId, cartItems.Length);

    var (validated, errors) = await ValidateProducts(cartItems);
    if (errors.Count > 0)
    {
        logger.LogWarning("checkout_validation_failed {UserId} {ErrorCount}", userId, errors.Count);
        return (null, new { Error = "checkout validation failed", Details = errors }, 409);
    }

    var subtotal = validated.Sum(e => e.Product.Price * e.Quantity);
    var tax = Math.Round(subtotal * TaxRate, 2);
    var total = Math.Round(subtotal + ShippingCost + tax, 2);

    var (payData, payError) = await ProcessPayment(userId, total);
    if (payError is not null)
    {
        logger.LogWarning("checkout_payment_failed {UserId} {Total} {Error}", userId, total, payError);
        var statusCode = payError == "payment service unavailable" ? 502 : 402;
        var resolvedPayId = payData?.PaymentId ?? payData?.Id;
        return (null, new
        {
            Error = payError,
            PaymentId = resolvedPayId,
            Details = payData?.Error ?? (payError == "payment service unavailable" ? payError : "The bank declined the transaction")
        }, statusCode);
    }

    await UpdateStock(validated);

    var orderItems = validated.Select(e => new OrderItemInput(
        e.Product.Id, e.Product.Name, e.Product.ImageUrl ?? "", e.Product.Price, e.Quantity
    ));
    var orderId = repository.InsertOrder(userId, total, payData?.Id, orderItems);

    logger.LogInformation("checkout_completed {UserId} {OrderId} {Total}", userId, orderId, total);
    return (new CheckoutResponse("ok", orderId, payData?.Id, total, validated.Count), null, 200);
}
```

Trois événements métier sont couverts :

- **`checkout_started`** : systématiquement au début du checkout. Permet de compter les tentatives et d'identifier un `UserId` dans la trace même si l'opération échoue avant de produire un `orderId`.
- **`checkout_validation_failed`** : si un ou plusieurs produits ne passent pas la validation stock. Niveau `Warning` car c'est un problème fonctionnel récupérable (l'utilisateur peut modifier son panier).
- **`checkout_payment_failed`** : si la banque ou le service de paiement refuse. Niveau `Warning` — le 402 est un comportement métier attendu (~10 % des cas simulés par `bank-service`).
- **`checkout_completed`** : en fin de parcours nominal. Porte l'`orderId` et le `total` comme attributs structurés.

### 4. Rebuilder et redémarrer

```bash
task up
```

### 5. Générer une requête

```bash
PRODUCT_ID=$(curl -s http://localhost:8080/api/products | \
  python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")

curl -X POST http://localhost:8080/api/billing/checkout \
  -H "Content-Type: application/json" \
  -d "{\"user_id\": \"2\", \"items\": [{\"product_id\": \"$PRODUCT_ID\", \"quantity\": 1}]}"
```

### 6. Observer les logs

```bash
task logs -- billing-service
```

Tu verras **deux lignes** pour chaque log applicatif :

1. La **ligne ASP.NET Core classique** (provider console natif) :

   ```text
   info: BillingService.BillingService[0]
         checkout_started 2 1
   ```

2. Le **LogRecord OTel** exporté par le `ConsoleLogExporter` — format `key:value` (pas JSON) :

   ```text
   LogRecord.Timestamp:               2026-05-04T15:17:31.4500000Z
   LogRecord.TraceId:                 5fa7c4e865f11fc66feafc85b954979e
   LogRecord.SpanId:                  1945a109f73dbd0f
   LogRecord.TraceFlags:              Recorded
   LogRecord.CategoryName:            BillingService.BillingService
   LogRecord.Severity:                Info
   LogRecord.SeverityText:            Information
   LogRecord.FormattedMessage:        checkout_started 2 1
   LogRecord.Body:                    checkout_started {UserId} {ItemCount}
   LogRecord.Attributes (Key:Value):
       UserId: 2
       ItemCount: 1
       OriginalFormat (a.k.a Body): checkout_started {UserId} {ItemCount}
   LogRecord.ScopeValues (Key:Value):
   [Scope.0]:SpanId: 1945a109f73dbd0f
   [Scope.0]:TraceId: 5fa7c4e865f11fc66feafc85b954979e
   [Scope.0]:ParentId: f44e2fe5990cca9a
   [Scope.1]:ConnectionId: 0HNL9U11G0RRA
   [Scope.2]:RequestId: 0HNL9U11G0RRA:00000001
   [Scope.2]:RequestPath: /checkout

   Resource associated with LogRecord:
   service.name: billing-service
   service.version: 1.0.0
   service.instance.id: 2e56c9f7-83f8-44bd-962d-3aab95fbd88a
   telemetry.sdk.name: opentelemetry
   telemetry.sdk.language: dotnet
   telemetry.sdk.version: 1.12.0
   ```

Points clés du format réel :

- **`LogRecord.TraceId` / `LogRecord.SpanId`** : le même `trace_id` que le span `POST /checkout` — la corrélation est automatique. Dans Grafana Tempo, un clic sur le span HTTP ouvre directement les logs associés.
- **`LogRecord.Body`** : le template brut du message (`checkout_started {UserId} {ItemCount}`), conservé grâce à `ParseStateValues = true`.
- **`LogRecord.FormattedMessage`** : le message interpolé (`checkout_started 2 1`), disponible grâce à `IncludeFormattedMessage = true`.
- **`LogRecord.Attributes`** : les paramètres structurés (`UserId`, `ItemCount`) extraits du template. Ce sont eux qui sont indexables côté Loki — on peut filtrer `{UserId="2"}` sans parser le texte.
- **`LogRecord.ScopeValues`** : les scopes ASP.NET Core (connexion, requête), capturés grâce à `IncludeScopes = true`. Le `ParentId` permet de remonter au span parent dans l'arbre de traces.

Tu verras également des `LogRecord` pour les logs d'infrastructure ASP.NET Core (démarrage du serveur, requêtes HTTP) avec `LogRecord.CategoryName: Microsoft.AspNetCore.Hosting.Diagnostics` — ce sont les mêmes logs qu'ASP.NET Core émet nativement, maintenant aussi bridgés vers OTel.

## Critères de validation

- [ ] `billing-service` démarre sans erreur après l'ajout du `ILogger<BillingService>`.
- [ ] Un `POST /api/billing/checkout` produit un `LogRecord` OTel avec l'événement `checkout_started` visible dans `task logs -- billing-service`, avec un `trace_id` non nul.
- [ ] Le `trace_id` du `LogRecord` correspond au `trace_id` du span `POST /checkout` dans la même sortie console.
- [ ] Une requête refusée par la banque (réponse `402`) produit un `LogRecord` avec l'événement `checkout_payment_failed`.

## Pour aller plus loin

- **Scopes structurés** : `using (logger.BeginScope(new { UserId = userId, OrderId = orderId }))` permet d'ajouter des attributs communs à tous les logs émis dans un bloc, sans les répéter sur chaque appel. Avec `IncludeScopes = true`, ces attributs se retrouvent dans chaque `LogRecord` du scope.
- **Niveaux de log OTel** : les niveaux `ILogger` sont mappés sur `SeverityNumber` OTel (`DEBUG=5`, `INFO=9`, `WARN=13`, `ERROR=17`). On peut filtrer par `severity_number >= 13` côté Loki pour n'exposer que les warnings et erreurs.
- **Corrélation inter-services** : le `trace_id` est propagé automatiquement par `AddHttpClientInstrumentation()`. Un log émis dans `payment-service` ou `bank-service` pendant le même checkout porte le **même `trace_id`**. Dans Grafana, on peut récupérer tous les logs cross-service d'une requête avec une seule requête LogQL : `{job=~"billing-service|payment-service"} | trace_id = "a1b2c3..."`.

## Étape suivante

Une fois l'exercice validé, direction [step4-add-collector.md](step4-add-collector.md).

Sur la branche `step-4-collector` :

- `billing-service` garde le bridge logging OTel.
- Les services Python ont leur instrumentation de logs configurée.
- L'exercice 4 portera sur la mise en place du **collecteur OpenTelemetry** pour sortir les trois signaux de la console et les envoyer vers Grafana (Tempo pour les traces, Loki pour les logs, VictoriaMetrics pour les métriques).
