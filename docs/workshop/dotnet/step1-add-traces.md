# Step 1 — Instrumentation des traces (`billing-service`)

> Branche : `main`
> Durée estimée : 30 min
> État à l'arrivée : `billing-service` non instrumenté. Le `bank-service` (Go) est **déjà instrumenté** par l'animateur — SDK + `otelgrpc` côté serveur, export console. C'est ce qui va permettre d'observer la propagation de contexte distribuée dès cette première étape, sans écrire une ligne de Go.

## Ce que tu vas apprendre

- Installer et configurer le **SDK OpenTelemetry .NET** dans un service (`AddOpenTelemetry()`, `ConfigureResource`, `WithTracing`).
- Instrumenter un serveur **ASP.NET Core Minimal API** avec `AddAspNetCoreInstrumentation()`.
- Instrumenter tous les **clients HTTP sortants** avec `AddHttpClientInstrumentation()` — une seule ligne couvre les trois `HttpClient` nommés du service.
- Observer en direct la **propagation de contexte W3C Trace Context** : le `trace_id` produit par `billing-service` se retrouve côté `bank-service` via les headers HTTP.

> Pour cette première étape, on reste simple : les spans sont exportés **sur la console** (stdout du conteneur). Le branchement vers le collecteur viendra plus tard.

## Pourquoi programmatique après le step 0 ?

Le step 0 montrait l'**auto-instrumentation zero-code** : configuration externe, zéro ligne de code. Pratique — mais opaque, et limité à ce que les instrumenteurs supportent automatiquement.

L'approche **programmatique** consiste à **importer les packages NuGet dans ton projet** et à les activer au démarrage du host. Elle est :

- **Explicite** : tu vois exactement ce qui est instrumenté.
- **Portable** : Docker, bare-metal, k8s sans operator — ça marche partout.
- **Extensible** : base indispensable pour ajouter des spans custom, des attributs métier, des enrichisseurs, ou des samplers.

## Pourquoi `billing-service` pour commencer ?

Parce qu'il est au **cœur de la chaîne** et regroupe les deux rôles les plus intéressants à instrumenter : serveur HTTP entrant **et** clients HTTP sortants multiples.

```text
UI → billing → products-service   (HTTP - validation stock)
            → payment-service    (HTTP - traitement paiement)
               └── bank-service  (gRPC - déjà instrumenté en Go)
```

En instrumentant `billing-service`, on déclenche d'un coup :

1. Un span **serveur HTTP** pour la requête `POST /checkout`.
2. Jusqu'à **trois spans clients HTTP** (products, payment, users) en enfants du premier.
3. Des headers `traceparent` injectés automatiquement dans chaque requête HTTP sortante → les services en aval qui lisent ce header peuvent propager la trace.

**Note sur la chaîne dans `main`** : sur cette branche, `payment-service` n'est pas encore instrumenté. La trace de `bank-service` apparaîtra donc **isolée** (trace_id différent) dans ses logs — c'est intentionnel. Ce n'est pas un bug : c'est exactement l'illustration de ce qu'apporte la propagation. La chaîne complète billing → payment → bank sera visible une fois `payment-service` instrumenté (step-2-metrics).

## Exercice

### 1. Ajouter les packages NuGet

Modifier [billing-service/billing-service.csproj](../../../src/billing-service/billing-service.csproj) — ajouter les quatre références dans le bloc `<ItemGroup>` existant :

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
  <PackageReference Include="OpenFeature" Version="2.*" />
  <PackageReference Include="OpenFeature.Contrib.Providers.Flagd" Version="0.5.*" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
  <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.12.0" />
  <PackageReference Include="OpenTelemetry.Exporter.Console" Version="1.12.0" />
</ItemGroup>
```

> **À propos des versions** : `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http` et `OpenTelemetry.Exporter.Console` sont tous alignés sur la même version `1.12.0`. C'est la version stable courante du SDK .NET OTel au moment de cet atelier — les packages d'instrumentation .NET suivent maintenant le **versioning stable** (contrairement au SDK Python qui utilise encore des versions `0.x` pour les instrumenteurs).

### 2. Configurer le TracerProvider dans Program.cs

Modifier [billing-service/Program.cs](../../../src/billing-service/Program.cs) — ajouter les `using` en tête de fichier et le bloc de configuration **avant** `var app = builder.Build()` :

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using BillingService;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ... (configuration existante inchangée) ...

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(
            serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "billing-service",
            serviceVersion: Environment.GetEnvironmentVariable("OTEL_SERVICE_VERSION") ?? "1.0.0"))
    .WithTracing(b => b
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();
```

Points d'attention :

- **`AddOpenTelemetry()` dans le conteneur DI** : c'est `IServiceCollection` qui héberge le `TracerProvider`. Il est créé au démarrage du host et détruit à l'arrêt — le `using` sur le provider est géré automatiquement.
- **`ConfigureResource()`** : peuple l'attribut `service.name` (et `service.version`) sur tous les spans émis. C'est ce champ que Grafana/Tempo utilise pour filtrer par service.
- **`AddAspNetCoreInstrumentation()`** : instrumente automatiquement toutes les routes Minimal API — `GET /health`, `POST /summary`, `POST /checkout`, `GET /orders/{userId}`, `GET /orders`.
- **`AddHttpClientInstrumentation()`** : instrumente **tous** les `HttpClient` créés via `IHttpClientFactory`, y compris les clients nommés `"products"` et `"payment"` déjà enregistrés. Un seul appel couvre les trois clients.
- **`AddConsoleExporter()`** : écrit chaque span terminé sur stdout dès qu'il se ferme (`SimpleExportProcessor` implicite). Parfait pour le debug, pas pour la prod.

### 3. Nommer le service (optionnel)

Le `ConsoleSpanExporter` n'a besoin d'aucune configuration réseau. Seule variable utile : `OTEL_SERVICE_NAME`, lue par `Program.cs` pour peupler l'attribut `service.name`. La valeur par défaut (`billing-service`) suffit.

**En Docker Compose** — dans [docker-compose.yml](../../../docker-compose.yml), bloc `billing-service` :

```yaml
billing-service:
  # ...
  environment:
    - FLAGD_HOST=flagd
    - FLAGD_PORT=8013
    - OTEL_SERVICE_NAME=billing-service
```

> **Note** : `PRODUCTS_SERVICE_URL`, `PAYMENT_SERVICE_URL` et `DATABASE_PATH` ne sont pas à ajouter — `Program.cs` définit des valeurs par défaut (`http://products-service:8002`, etc.) qui correspondent déjà aux noms de services Docker. Seule `OTEL_SERVICE_NAME` est nouvelle ici.

### 4. Rebuilder et redémarrer

```bash
task up
```

### 5. Générer du trafic

Une seule requête suffit pour cette étape — c'est plus facile de lire une trace isolée dans les logs.

```bash
# Récupère le premier product_id disponible
PRODUCT_ID=$(curl -s http://localhost:8080/api/products | \
  python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")

curl -X POST http://localhost:8080/api/billing/checkout \
  -H "Content-Type: application/json" \
  -d "{\"user_id\": \"2\", \"items\": [{\"product_id\": \"$PRODUCT_ID\", \"quantity\": 1}]}"
```

Réponses attendues :

- `200 OK` avec un JSON `{ "order_id": ..., "total": ..., "status": "approved", ... }` si la banque approuve,
- `402 Payment Required` si la banque décline (~10 % des cas simulés par `bank-service`) — comportement métier attendu, pas un bug,
- `409 Conflict` si le produit est en rupture de stock.

### 6. Observer les spans côté `billing-service`

```bash
task logs -- billing-service
```

Tu verras plusieurs spans (format .NET, JSON multi-lignes) :

- un **serveur HTTP** `POST /checkout` (kind `SERVER`, attributs `http.request.method=POST`, `http.route=/checkout`, `http.response.status_code`),
- un **client HTTP** vers `products-service` pour valider le stock (kind `CLIENT`, attributs `http.request.method`, `url.full`, `http.response.status_code`) — enfant du span serveur,
- un **client HTTP** vers `payment-service` pour traiter le paiement (kind `CLIENT`) — enfant du span serveur,
- selon le code de `BillingService.cs`, potentiellement un client HTTP vers `users-service`.

Tous ces spans partagent le **même `trace_id`**. C'est la chaîne de causalité reconstituée depuis un seul processus.

### 7. Observer côté `bank-service`

```bash
task logs -- bank-service
```

Tu verras un span `transaction.TransactionService/ProcessTransaction` (kind `SERVER`) émis par `otelgrpc` en Go. Son `Parent.Remote = true`.

**Sur `main`, `payment-service` n'est pas instrumenté** : il reçoit bien le header `traceparent` de `billing-service`, mais ne le transmet pas à `bank-service` via les métadonnées gRPC. `bank-service` démarre donc une **nouvelle trace isolée** avec un `trace_id` différent de celui de `billing-service`. Dans Grafana/Tempo, tu verrais deux petits arbres séparés au lieu d'un arbre complet.

Ce n'est pas un bug : c'est précisément l'illustration de ce qu'apporte l'instrumentation de chaque maillon de la chaîne. Une fois `payment-service` instrumenté (step-2-metrics), les `trace_id` de `billing`, `payment` et `bank` seront identiques.

## Analyse : l'arbre de spans sur step-2-metrics

Une fois tous les services instrumentés, une requête `POST /checkout` produit l'arbre suivant :

```text
trace_id = a1b2c3d4e5f6789012345678abcdef01
│
└─ POST /checkout                                              [billing, SERVER]     ← racine
   span_id     = 1111111111111111
   parent_id   = null
   service     = billing-service
   attrs       : http.request.method=POST, http.route=/checkout, http.response.status_code=200
   │
   ├─ GET http://products-service:8002/products/1             [billing, CLIENT]
   │  span_id     = 2222222222222222
   │  parent_id   = 1111111111111111
   │  service     = billing-service
   │  attrs       : http.request.method=GET, url.full=..., http.response.status_code=200
   │
   └─ POST http://payment-service:8003/payments               [billing, CLIENT]
      span_id     = 3333333333333333
      parent_id   = 1111111111111111
      service     = billing-service
      attrs       : http.request.method=POST, url.full=..., http.response.status_code=201 (ou 402 si refus)
      │
      └─ POST /payments                                        [payment, SERVER]
         span_id     = 4444444444444444
         parent_id   = 3333333333333333  (Remote: TRUE)        ← traceparent HTTP reçu de billing
         service     = payment-service
         attrs       : http.request.method=POST, http.route=/payments, http.response.status_code=201 (ou 402 si refus)
         │
         └─ /transaction.TransactionService/ProcessTransaction [payment, CLIENT gRPC]
            span_id     = 5555555555555555
            parent_id   = 4444444444444444
            service     = payment-service
            │
            └─ transaction.TransactionService/ProcessTransaction [bank, SERVER gRPC]
               SpanID      = 6666666666666666
               Parent.ID   = 5555555555555555  (Remote: TRUE)  ← traceparent gRPC reçu de payment
               service     = bank-service
               instrumentation : otelgrpc
```

### Les règles de lecture

1. **Un `trace_id` unique** circule sur l'intégralité de l'arbre, de `billing` jusqu'à `bank`. C'est l'identifiant de la requête de bout en bout — c'est lui qu'on utilise dans Tempo pour retrouver toute la trace.
2. **`parent_id` / `Parent.SpanID` pointe vers le `span_id` du parent.** Tu peux reconstituer l'arbre à la main depuis les logs console.
3. **`Remote: true` (côté receveur)** est le marqueur-clé de la propagation cross-process : « mon parent n'est pas dans mon processus, il m'a été transmis par un header entrant. » C'est la preuve que W3C Trace Context a fonctionné. Ce marqueur apparaît côté `payment` (qui reçoit le `traceparent` HTTP de `billing`) et côté `bank` (qui reçoit le `traceparent` gRPC de `payment`).
4. **`SpanKind`** indique le rôle dans l'échange :
   - `SERVER` : je reçois une requête (HTTP ou gRPC server).
   - `CLIENT` : j'émets une requête (HTTP ou gRPC client).
   - `INTERNAL` : span purement local (logique métier custom — on en verra aux étapes suivantes).

### Lecture des timestamps

```text
billing  POST /checkout               [T+0ms  → T+350ms]
billing  GET  products-service        [T+5ms  → T+20ms ]  ⊂ parent
billing  POST payment-service         [T+25ms → T+340ms]  ⊂ parent
payment  POST /payments               [T+28ms → T+338ms]  ⊂ parent (+ ~3ms réseau)
payment  gRPC CLIENT bank             [T+32ms → T+335ms]  ⊂ parent
bank     gRPC SERVER ProcessTx        [T+35ms → T+333ms]  ⊂ parent (+ ~3ms réseau, 100-500ms délai simulé)
```

Chaque enfant commence **après** son parent et finit **avant**. Le décalage entre un span CLIENT et le span SERVER correspondant (~3 ms) représente le temps de traversée réseau — c'est exactement ce qu'on cherche à mesurer en tracing distribué.

## Critères de validation

- [ ] `billing-service` démarre sans erreur avec les nouveaux packages NuGet.
- [ ] Un `POST /api/billing/checkout` produit **au moins deux spans** dans la console de `billing-service` : un span serveur HTTP `POST /checkout` et un ou plusieurs spans clients HTTP enfants.
- [ ] Tous les spans du même appel partagent le **même `trace_id`**.
- [ ] Dans la console de `bank-service`, un span gRPC SERVER apparaît avec `Parent.Remote = true` (preuve que `payment-service`, même non instrumenté, a bien transmis le header `traceparent` reçu de `billing`).

## Pour aller plus loin

- **Filtrer les health checks** : `AddAspNetCoreInstrumentation(o => o.Filter = ctx => ctx.Request.Path != "/health")` pour ne pas polluer les traces avec les sondes Docker.
- **Enrichir les spans `HttpClient`** : `AddHttpClientInstrumentation(o => o.EnrichWithHttpRequestMessage = (activity, req) => activity.SetTag("billing.target", req.RequestUri?.Host))` pour identifier le service cible dans chaque span client.
- **W3C Trace Context** : le header `traceparent` suit le format [W3C Trace Context](https://www.w3.org/TR/trace-context/) (`00-{trace_id}-{parent_span_id}-{flags}`). C'est ce standard commun qui permet l'interopérabilité entre .NET (OTel SDK), Python (OTel SDK) et Go (OTel SDK) — et n'importe quel autre runtime instrumenté.

## Étape suivante

Une fois l'exercice validé, direction [step2-add-metrics.md](step2-add-metrics.md).

Sur la branche `step-2-metrics`, **tous** les services sont instrumentés pour les traces (préparé par l'animateur) — tu y trouveras l'énoncé de l'exercice 2 sur les **métriques**, et tu pourras enfin observer l'arbre de spans complet billing → payment → bank dans un seul graphe Grafana Tempo.
