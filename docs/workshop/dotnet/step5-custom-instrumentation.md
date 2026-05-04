# Étape 5 — Instrumentation custom : spans métier et métriques applicatives

> Branche : `step-5-custom-metrics`
> Durée estimée : 35 min
> État à l'arrivée : les trois signaux (traces, métriques, logs) sortent via le collecteur vers Grafana local (Tempo, VictoriaMetrics, Loki). La télémétrie actuelle est purement **technique** : elle décrit le protocole (HTTP, ASP.NET Core), la structure (serveur, clients HTTP), mais pas le **métier**. On va enrichir ça.

## Préparation : passer sur la branche `step-5-custom-metrics`

Tu arrives de l'exo 4 avec les trois signaux qui transitent vers Grafana. On les **écarte** pour récupérer l'état propre de `step-5-custom-metrics` (solution de référence de l'exo 4 + point de départ de l'exo 5).

```bash
# 1. Annule toutes les modifications locales sur les fichiers suivis
git reset --hard

# 2. Supprime les fichiers non suivis
git clean -fd

# 3. Bascule sur la branche de l'exo 5
git checkout step-5-custom-metrics
```

> Ces commandes **détruisent** ton travail local de l'exo 4. Si tu veux conserver ta solution, fais d'abord un `git stash` ou un commit sur une branche perso avant la réinitialisation.

## Ce que tu vas apprendre

- Créer des **spans custom** (manuels) avec `ActivitySource` pour capturer des concepts métier : étapes du checkout, décisions (approbation, déclin), latences métier.
- Enrichir les spans avec des **attributs métier** (`SetTag`) : identifiants utilisateur, montants, états de validation.
- Créer des **événements** (`AddEvent`) pour marquer les transitions critiques à l'intérieur d'un span.
- Ajouter des **métriques custom** avec `System.Diagnostics.Metrics.Meter` : compteurs (`Counter<long>`), histogrammes (`Histogram<double>`).
- Enregistrer `ActivitySource` et `Meter` dans le pipeline OTel via `AddSource()` et `AddMeter()` — et comprendre **pourquoi** cette étape est nécessaire.
- Comprendre la différence entre **erreurs techniques** (service indisponible) et **erreurs métier** (stock insuffisant, paiement refusé) — et comment les représenter différemment dans OTel.
- Activer les **exemplars** pour relier une mesure métrique à la trace exacte qui l'a produite — corrélation métriques → traces en un clic dans Grafana.
- Visualiser et corréler ces signaux métier dans Grafana pour **alerter intelligemment**.

---

## Partie A — Spans custom avec ActivitySource

L'auto-instrumentation ASP.NET Core donne déjà un span pour `POST /checkout`. Mais elle n'en sait rien des étapes internes : validation du stock, calcul du total, appel au service de paiement, enregistrement de la commande. On va cartographier ça explicitement.

### A.1. Déclarer ActivitySource et métriques dans BillingService.cs

Modifier [`BillingService.cs`](../../../src/billing-service/BillingService.cs) — ajouter les using et les champs statiques en tête de classe :

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

// Dans la classe BillingService :
private static readonly ActivitySource ActivitySource = new("billing-service");
private static readonly Meter Meter = new("billing-service");
private static readonly Counter<long> CheckoutsTotal =
    Meter.CreateCounter<long>("checkouts.total", description: "Total checkout requests");
private static readonly Counter<long> CheckoutsDeclined =
    Meter.CreateCounter<long>("checkouts_declined.total", description: "Declined checkouts");
private static readonly Histogram<double> CheckoutDuration =
    Meter.CreateHistogram<double>("checkout.duration", unit: "ms", description: "Checkout processing duration");
```

Points clés :

- **`ActivitySource`** : le point d'entrée pour créer des spans custom en .NET. Pense-y comme au `tracer` d'OpenTelemetry Python.
- **`Meter`** : le point d'entrée pour créer des métriques custom.
- Les champs sont `static readonly` : un seul `ActivitySource` et `Meter` par processus suffit (et c'est la pratique recommandée).
- Le nom `"billing-service"` doit correspondre exactement à ce qui est enregistré dans `Program.cs` (voir section suivante).

### A.2. Enregistrer dans Program.cs

Modifier [`Program.cs`](../../../src/billing-service/Program.cs) — s'assurer que les lignes d'enregistrement sont présentes dans la configuration OTel :

```csharp
// Dans la configuration du tracing :
.WithTracing(tracing => tracing
    .AddSource("billing-service")   // enregistre notre ActivitySource custom
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    // ...
)

// Dans la configuration des métriques :
.WithMetrics(metrics => metrics
    .AddMeter("billing-service")    // enregistre notre Meter custom
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    // ...
)
```

**Pourquoi cette étape est nécessaire ?** Le SDK OTel .NET ne collecte pas automatiquement tous les `ActivitySource` et `Meter` présents dans le processus — cela éviterait d'exporter des métriques internes de librairies tierces non désirées. Tu dois explicitement déclarer les sources que tu veux observer.

### A.3. Modéliser les outcomes avec des enums

Avant d'instrumenter, on définit le vocabulaire métier qui servira aux tags. Plutôt que de disperser des magic strings (`"approved"`, `"validation_failed"`…) dans le code, on les centralise dans des enums avec une seule fonction de conversion vers la valeur de tag.

Dans [`BillingService.cs`](../../../src/billing-service/BillingService.cs), ajouter les enums et constantes en tête de classe :

```csharp
private const string PaymentServiceUnavailable = "payment_service_unavailable";
private const string PaymentDeclined = "payment declined";

private enum CheckoutOutcome
{
    Attempted,
    Approved,
    Declined
}

private enum DeclineReason
{
    SimulatedFailure,
    ValidationFailed,
    ServiceUnavailable,
    BankDeclined
}

private static string ToTagValue(CheckoutOutcome outcome) => outcome switch
{
    CheckoutOutcome.Attempted => "attempted",
    CheckoutOutcome.Approved => "approved",
    CheckoutOutcome.Declined => "declined",
    _ => throw new ArgumentOutOfRangeException(nameof(outcome))
};

private static string ToTagValue(DeclineReason reason) => reason switch
{
    DeclineReason.SimulatedFailure => "simulated_failure",
    DeclineReason.ValidationFailed => "validation_failed",
    DeclineReason.ServiceUnavailable => "service_unavailable",
    DeclineReason.BankDeclined => "bank_declined",
    _ => throw new ArgumentOutOfRangeException(nameof(reason))
};
```

Le code métier ne manipule plus que des `CheckoutOutcome.Approved` ou `DeclineReason.BankDeclined`. La conversion vers la string Prometheus se fait à un seul endroit — refactor sans risque, autocomplétion partout.

### A.4. Helpers de métriques

Trois opérations reviennent à chaque sortie de `Checkout` : incrémenter le compteur, enregistrer la durée, et — sur les déclines — incrémenter le compteur dédié avec la raison. On les regroupe dans des helpers privés :

```csharp
private static void IncreaseCheckoutTotal(CheckoutOutcome outcome)
{
    CheckoutsTotal.Add(1, new TagList { { "status", ToTagValue(outcome) } });
}

private static void RecordDuration(long startTimestamp, CheckoutOutcome outcome)
{
    var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
    CheckoutDuration.Record(elapsed.TotalMilliseconds, new TagList { { "outcome", ToTagValue(outcome) } });
}

private static void RecordDecline(long startTime, DeclineReason reason)
{
    IncreaseCheckoutTotal(CheckoutOutcome.Declined);
    RecordDuration(startTime, CheckoutOutcome.Declined);
    CheckoutsDeclined.Add(1, new TagList { { "reason", ToTagValue(reason) } });
}
```

`RecordDecline` est appelé sur les **trois** chemins de decline (validation, paiement, simulation). DRY appliqué à l'instrumentation.

### A.5. Instrumenter par responsabilité

Plutôt que de centraliser tous les spans dans `Checkout`, **chaque méthode possède son span**. C'est plus propre : l'instrumentation vit là où le travail vit, et `Checkout` reste lisible comme une orchestration.

#### `ValidateProducts` → span `validate.stock`

```csharp
public async Task<(List<ValidatedItem> Validated, List<ValidationError> Errors)> ValidateProducts(CartItem[] items)
{
    using var validateActivity = ActivitySource.StartActivity("validate.stock");
    validateActivity?.SetTag("validation.items_count", items.Length);

    var validated = new List<ValidatedItem>();
    var errors = new List<ValidationError>();

    foreach (var item in items)
    {
        // ... appel HTTP au products-service, accumulation dans validated/errors
    }

    validateActivity?.SetTag("validation.errors", errors.Count);
    validateActivity?.SetTag("validation.status", errors.Count > 0 ? "failed" : "passed");

    if (errors.Count > 0)
    {
        validateActivity?.AddEvent(new ActivityEvent("validation_failed",
            tags: new ActivityTagsCollection { { "error_count", errors.Count } }));
    }

    return (validated, errors);
}
```

#### `ProcessPayment` → span `process.payment`

Le span est entièrement géré par `ProcessPayment`. Noter le `SetStatus(Error)` **uniquement** sur le chemin technique (service injoignable) — pas sur un refus bancaire (cf. Partie B).

```csharp
public async Task<(PaymentDto? Data, string? Error)> ProcessPayment(string userId, double amount)
{
    using var payActivity = ActivitySource.StartActivity("process.payment");
    payActivity?.SetTag("user.id", userId);
    payActivity?.SetTag("payment.total", amount);

    HttpResponseMessage res;
    try
    {
        res = await PaymentClient.PostAsJsonAsync(/* ... */);
    }
    catch
    {
        payActivity?.SetTag("payment.approved", false);
        payActivity?.SetTag("payment.decline_reason", PaymentServiceUnavailable);
        payActivity?.SetStatus(ActivityStatusCode.Error, PaymentServiceUnavailable);
        payActivity?.AddEvent(new ActivityEvent("payment_service_error",
            tags: new ActivityTagsCollection { { "reason", PaymentServiceUnavailable } }));
        return (null, PaymentServiceUnavailable);
    }

    var data = await res.Content.ReadFromJsonAsync<PaymentDto>(JsonOpts);
    if ((int)res.StatusCode != 201 || data?.Status != "approved")
    {
        payActivity?.SetTag("payment.approved", false);
        return (data, PaymentDeclined);
    }

    payActivity?.SetTag("payment.approved", true);
    return (data, null);
}
```

#### `ComputeOrderTotal`, `SaveOrder`, `UpdateStock` → spans `compute.total`, `save.order`, `update.stock`

Chaque méthode courte ouvre son propre span avec `using`, pose ses tags, et c'est tout. Exemple typique :

```csharp
private static double ComputeOrderTotal(List<ValidatedItem> validated)
{
    using var totalActivity = ActivitySource.StartActivity("compute.total");
    var subtotal = validated.Sum(e => e.Product.Price * e.Quantity);
    var tax = Math.Round(subtotal * TaxRate, 2);
    var total = Math.Round(subtotal + ShippingCost + tax, 2);
    totalActivity?.SetTag("subtotal", subtotal);
    totalActivity?.SetTag("tax", tax);
    totalActivity?.SetTag("shipping", ShippingCost);
    totalActivity?.SetTag("total", total);
    return total;
}
```

### A.6. Orchestrer le checkout

Avec les responsabilités extraites et les helpers en place, `Checkout` devient un script lisible en une lecture :

```csharp
public async Task<(CheckoutResponse? Result, object? Error, int StatusCode)> Checkout(string userId, CartItem[] cartItems)
{
    var startTime = Stopwatch.GetTimestamp();

    using var checkoutActivity = ActivitySource.StartActivity("process.checkout");
    checkoutActivity?.SetTag("user_id", userId);
    checkoutActivity?.SetTag("checkout.items_count", cartItems.Length);

    IncreaseCheckoutTotal(CheckoutOutcome.Attempted);
    logger.LogInformation("checkout_started {UserId} {ItemCount}", userId, cartItems.Length);

    await ThrowIfSimulatedFailure(checkoutActivity, userId, startTime);
    await ApplyMemoryLeakSimulation(checkoutActivity, userId);

    var (validated, errors) = await ValidateProducts(cartItems);
    if (errors.Count > 0)
        return DeclineForValidation(checkoutActivity, userId, startTime, errors);

    var total = ComputeOrderTotal(validated);

    var (payData, payError) = await ProcessPayment(userId, total);
    if (payError is not null)
        return DeclineForPayment(userId, startTime, total, payData, payError);

    await UpdateStock(validated);
    var orderId = SaveOrder(userId, total, validated, payData?.Id);

    return ApproveCheckout(checkoutActivity, userId, startTime, total, orderId, payData, validated.Count);
}
```

Les trois sorties decline appellent toutes `RecordDecline(startTime, DeclineReason.X)` puis construisent leur réponse HTTP. Exemple pour la validation :

```csharp
private (CheckoutResponse? Result, object? Error, int StatusCode) DeclineForValidation(
    Activity? activity, string userId, long startTime, List<ValidationError> errors)
{
    logger.LogWarning("checkout_validation_failed {UserId} {ErrorCount}", userId, errors.Count);
    activity?.SetStatus(ActivityStatusCode.Error);
    activity?.AddEvent(new ActivityEvent("checkout_validation_failed"));
    RecordDecline(startTime, DeclineReason.ValidationFailed);
    return (null, new { Error = "checkout validation failed", Details = errors }, 409);
}
```

Points clés à retenir :

- **`ActivitySource.StartActivity("process.checkout")`** : crée un span qui devient automatiquement **enfant** du span ASP.NET Core courant (contexte propagé via `Activity.Current`). Les spans créés dans `ValidateProducts`, `ProcessPayment`, etc. seront à leur tour enfants de `process.checkout`.
- **`using var`** / **`using (...)`** : le span est terminé à la fin du bloc. Pas besoin d'appeler `.Stop()` explicitement.
- **`?.SetTag(key, value)`** : null-conditionnel car `StartActivity()` retourne `null` si le sampling décide de ne pas tracer. Le code fonctionnel ne dépend jamais d'un span.
- **`AddEvent(new ActivityEvent(...))`** : marque un événement horodaté **à l'intérieur** du span — visible dans Tempo quand tu déplis le span. Utile pour les transitions qui ne méritent pas leur propre span.
- **`SetStatus(ActivityStatusCode.Error, description)`** : marque le span en erreur. À utiliser uniquement pour les erreurs **techniques** (voir Partie B).
- **`Stopwatch.GetTimestamp()` / `GetElapsedTime()`** : mesure de durée haute précision, sans allocation d'objet `DateTime`.
- **Instrumentation par responsabilité** : chaque méthode ouvre son propre span. `Checkout` n'instrumente plus que son propre niveau (`process.checkout`) et délègue le reste. C'est plus propre, plus testable, et l'observabilité ne pollue pas la logique métier.

### A.7. Rebuilder et tester

```bash
task up
```

Générer du trafic via un checkout :

```bash
# Checkout valide (après avoir créé un panier via l'UI ou l'API)
PRODUCT_ID=$(curl -s http://localhost:8080/api/products | \
  python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")

curl -X POST http://localhost:8080/api/billing/checkout \
  -H "Content-Type: application/json" \
  -d "{
    \"user_id\": \"2\",
    \"items\": [{\"product_id\": \"$PRODUCT_ID\", \"quantity\": 1}]
  }"
```

### A.8. Observer dans Grafana Tempo

Dans Grafana / Tempo, cherche une trace avec `service.name = billing-service`. L'arbre de spans doit maintenant contenir :

```
POST /checkout  (ASP.NET Core auto-instrument)
└── process.checkout
    ├── validate.stock
    │   └── GET http://products-service:8002/products/{id}  (HttpClient auto-instrument, × N)
    ├── compute.total
    ├── process.payment
    │   └── POST http://payment-service:8003/payments  (HttpClient auto-instrument)
    ├── update.stock
    │   └── PATCH http://products-service:8002/products/{id}/stock  (HttpClient auto-instrument, × N)
    └── save.order
```

Déplie chaque span pour voir ses attributs. Par exemple, `process.payment` doit montrer un tag `payment.approved` et, en cas d'échec, un événement `payment_service_error`.

---

## Partie B — Distinction erreurs techniques vs erreurs métier

Un piège courant : marquer un "paiement refusé par la banque" comme une **erreur de span** (ce qui déclenche des alertes, ping l'on-call). Mais c'est une **réponse métier valide**, pas un bug technique.

| Situation | Code HTTP | `SetStatus(Error)` ? | Raison |
|---|---|---|---|
| Stock insuffisant | 409 Conflict | Non | Réponse métier attendue |
| Paiement refusé par la banque | 402 Payment Required | Non | Refus bancaire = cas nominal |
| Payment-service injoignable | 502 Bad Gateway | **Oui** | Défaillance technique externe |
| Exception non gérée | 500 Internal Server Error | **Oui** | Bug ou erreur système |

Dans `ProcessPayment`, `SetStatus(Error)` n'est appelé **que** dans le bloc `catch` — c'est-à-dire quand l'appel HTTP au payment-service a échoué (service tombé, timeout, etc.). Sur le chemin "réponse 4xx + status=declined" (refus bancaire), le span pose seulement `payment.approved = false` :

```csharp
catch
{
    payActivity?.SetTag("payment.approved", false);
    payActivity?.SetTag("payment.decline_reason", PaymentServiceUnavailable);
    payActivity?.SetStatus(ActivityStatusCode.Error, PaymentServiceUnavailable);  // ← erreur technique
    // ...
    return (null, PaymentServiceUnavailable);
}

// Plus bas, sur un refus bancaire :
if ((int)res.StatusCode != 201 || data?.Status != "approved")
{
    payActivity?.SetTag("payment.approved", false);  // ← pas de SetStatus(Error)
    return (data, PaymentDeclined);
}
```

Côté `Checkout`, la distinction se fait via la **constante partagée**, pas un littéral fragile :

```csharp
private const string PaymentServiceUnavailable = "payment_service_unavailable";
// ...
var isTechnicalError = payError == PaymentServiceUnavailable;
var reason = isTechnicalError ? DeclineReason.ServiceUnavailable : DeclineReason.BankDeclined;
```

> ⚠️ **Piège à éviter** : comparer à un littéral inline (`payError == "payment service unavailable"`) est ce qui s'écrit naturellement, mais une faute de frappe (espace au lieu d'underscore, casse différente…) rend la comparaison toujours fausse. Bug silencieux : tous les échecs sont alors classés comme refus bancaires, masquant les vraies pannes techniques dans les métriques.

Bénéfices :

1. SLA propres : pas de faux-positifs dans les alertes erreurs sur les refus bancaires.
2. Métriques séparées : `checkouts_declined_total{reason="service_unavailable"}` vs `{reason="bank_declined"}` deviennent deux séries distinctes alertables indépendamment.

---

## Partie C — Exemplars : relier métriques et traces

Les exemplars sont des échantillons de valeurs métriques **enrichis d'un contexte de trace** (`trace_id`, `span_id`). Dans Grafana, ils apparaissent comme des losanges sur les graphiques d'histogramme — clique dessus pour sauter directement à la trace correspondante dans Tempo. C'est la **corrélation métriques → traces** en un clic, sans recherche manuelle.

### C.1. Activer les exemplars dans le SDK .NET

Modifier [`Program.cs`](../../../src/billing-service/Program.cs) — ajouter `SetExemplarFilter` dans la configuration des métriques :

```csharp
// Dans la configuration des métriques :
.WithMetrics(metrics => metrics
    .SetExemplarFilter(ExemplarFilterType.TraceBased)  // exemplar attaché si un span actif existe
    .AddMeter("billing-service")
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    // ...
)
```

**Pourquoi `TraceBased` ?** Un exemplar est attaché à la mesure uniquement si un `Activity` actif existe au moment de l'appel — ce qui est presque toujours le cas dans notre pipeline HTTP. `AlwaysOn` surcharge le stockage, `AlwaysOff` désactive la fonctionnalité.

### C.2. Vérifier la propagation dans le collecteur

Les exemplars font partie du format OTLP metrics natif : le collecteur les transmet sans configuration particulière vers VictoriaMetrics. Aucune modification de [`otel-collector/otelcol-config.yaml`](../../../src/otel-collector/otelcol-config.yaml) n'est nécessaire.

Pour confirmer que les exemplars transitent, reconstruire et générer du trafic :

```bash
task up
```

Puis déclencher plusieurs checkouts (réussis et déclinés) pour accumuler des mesures dans l'histogramme `checkout_duration_ms`.

### C.3. Configurer la liaison Exemplars → Tempo dans Grafana

Dans Grafana : **Connections → Data sources → VictoriaMetrics** (ou ta source Prometheus) → onglet **Exemplars** :

- Cocher **"Enable"**
- **Internal link** → sélectionner la source de données **Tempo**
- **Label name** → `trace_id` (c'est le label qu'OTel injecte automatiquement)

Sauvegarder. Cette liaison indique à Grafana comment convertir un `trace_id` d'exemplar en requête Tempo.

### C.4. Visualiser les exemplars dans Grafana Explorer

Dans **Explore**, sélectionner la source VictoriaMetrics et exécuter :

```promql
histogram_quantile(0.95, rate(checkout_duration_ms_bucket[5m]))
```

Active l'interrupteur **"Exemplars"** (en haut du panneau de requête). Des **losanges jaunes** apparaissent sur la courbe — chaque losange est un exemplar : une mesure réelle avec son `trace_id` associé.

Clique sur un losange → **"Query with Tempo"** → Tempo s'ouvre directement sur la trace qui a produit cette valeur de latence.

### C.5. Cas d'usage : débugger un pic de latence

Scénario typique sans exemplars : le P95 monte à 800 ms, tu cherches manuellement des traces sur l'intervalle horaire, tu en lis plusieurs avant de trouver la coupable.

Avec exemplars :

1. Le graphique P95 de `checkout_duration_ms` monte à 800 ms.
2. Tu actives les exemplars — un losange apparaît à 1 200 ms sur la courbe.
3. Clic → Tempo affiche la trace : le span `process.payment` a duré 950 ms.
4. L'attribut `payment.decline_reason` révèle un timeout réseau vers `bank-service`.

**Gain** : quelques secondes au lieu de plusieurs minutes, et tu atterris sur la trace exacte, pas sur une trace représentative.

---

## Partie D — Observer les métriques custom dans Grafana

Les métriques `checkouts_total`, `checkouts_declined_total` et `checkout_duration_ms` sont maintenant exportées vers VictoriaMetrics/Prometheus. Dans Grafana Explorer (source VictoriaMetrics) :

**Compteur des checkouts approuvés :**
```promql
rate(checkouts_total{status="approved"}[5m])
```

**Taux de déclines (toutes raisons) :**
```promql
rate(checkouts_declined_total[5m])
```

**P95 de la durée de checkout :**
```promql
histogram_quantile(0.95, rate(checkout_duration_ms_bucket[5m]))
```

**Taux de déclines par raison :**
```promql
rate(checkouts_declined_total[5m]) by (reason)
```

Tu dois voir les séries augmenter avec le trafic généré. Les attributs (`status`, `reason`, `outcome`) deviennent des labels Prometheus exploitables pour les alertes.

---

## Critères de validation

- [ ] **Spans custom** : un `POST /checkout` génère 7 spans (HTTP root + `process.checkout` + `validate.stock` + `compute.total` + `process.payment` + `update.stock` + `save.order`).
- [ ] **Attributs métier** : chaque span affiche ses attributs (`user_id`, `checkout.items_count`, `total`…) dans Tempo.
- [ ] **Événements** : sur un checkout avec service injoignable, `process.payment` montre un événement `payment_service_error`.
- [ ] **Enums centralisés** : aucune magic string `"approved"` / `"declined"` / `"bank_declined"` ne reste dans `Checkout` — tout passe par `CheckoutOutcome` / `DeclineReason` et leurs `ToTagValue`.
- [ ] **SetStatus sélectif** : un checkout refusé par la banque (402) n'a **pas** de span en erreur rouge ; un checkout avec service injoignable (502) **a** un span en erreur.
- [ ] **Exemplars activés** : `ExemplarFilterType.TraceBased` est présent dans la config métriques de `Program.cs`.
- [ ] **Exemplars visibles** : dans Grafana Explorer, la requête `histogram_quantile(0.95, rate(checkout_duration_ms_bucket[5m]))` avec "Exemplars" activé affiche des losanges cliquables sur la courbe.
- [ ] **Navigation métriques → traces** : cliquer un losange d'exemplar ouvre la trace correspondante dans Tempo (liaison data source configurée).
- [ ] **Compteur `checkouts_total`** : les séries `{status="attempted"}` et `{status="approved"}` existent et augmentent en Grafana.
- [ ] **Compteur `checkouts_declined_total`** : la série avec les labels `reason` est visible et reflète les déclines.
- [ ] **Histogramme `checkout_duration`** : visible en Grafana, les quantiles (p50, p95) sont calculables via `histogram_quantile`.
- [ ] **Logs corrélés** : fais un checkout, cherche le `trace_id` dans Loki — tu trouves les logs `checkout_started` et `checkout_completed` de cette trace.

---

## Pour aller plus loin

- **`Activity.Current`** : dans n'importe quelle méthode de la call stack, `Activity.Current` donne accès au span actif — utile pour enrichir des spans créés plus haut dans la hiérarchie sans passer l'objet en paramètre.
- **Baggage** : `Activity.Current?.Baggage` permet de propager des valeurs métier à travers des frontières de services (HTTP, gRPC), similaire aux headers mais avec une sémantique OTel.
- **`ActivityLink`** : relie deux spans de traces différentes (utile pour des workflows asynchrones — un span de traitement lié au span de déclenchement).
- **SLOs** : « 95 % des checkouts approuvés < 500 ms » — requête Grafana sur `histogram_quantile(0.95, checkout_duration_ms_bucket)` avec alerte si > 500.
- **Exemplars sur les compteurs** : les exemplars fonctionnent aussi sur les `Counter<long>` — chaque `Add()` dans un span actif peut attacher un exemplar, permettant de naviguer depuis un pic de taux de déclines directement vers la trace incriminée.
- **Sampling conditionnel** : configurer un `tail_sampling` dans le collecteur pour garder 100 % des traces avec des déclines et seulement 5 % des succès — réduit le volume sans perdre les cas intéressants. Les exemplars pointent toujours vers des traces existantes, donc le sampling affecte leur disponibilité.

---

## Félicitations

Tu viens de compléter une **stack d'observabilité complète** pour le billing-service .NET :

- Traces auto + custom : `checkout.process` divisé en étapes métier, contexte distribué .NET → Python → Go.
- Métriques auto + custom : compteurs métier, histogrammes de latence, labels exploitables.
- Exemplars : corrélation directe métriques → traces — un clic sur un point d'histogramme ouvre la trace correspondante dans Tempo.
- Logs corrélés : chaque étape loggée avec `trace_id` automatiquement injecté (via l'instrumentation OTel Logging de l'exo 3).
- Collecteur : pipeline centralisé, enrichissement par étudiant, export Grafana.

Explore les dashboards Grafana et configure des alertes sur tes KPIs — tu as maintenant toute l'observabilité nécessaire pour débugger en production.
