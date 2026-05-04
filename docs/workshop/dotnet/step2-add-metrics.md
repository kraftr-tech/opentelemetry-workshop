# Étape 2 — Auto-instrumentation des métriques (`billing-service`)

> Branche : `step-2-metrics`
> Durée estimée : 15 min
> État à l'arrivée : tous les services Python sont instrumentés pour les **traces** (solution de l'exo 1), et `bank-service` (Go) est déjà instrumenté pour les trois signaux. Le `billing-service` lui-même a été instrumenté pour les traces à l'étape 1.

## Préparation : passer sur la branche `step-2-metrics`

Tu arrives de l'exo 1 avec des modifications locales sur `main` (les fichiers que tu as édités). On va les **écarter** pour récupérer l'état propre de la branche `step-2-metrics`, qui contient la solution de référence de l'exo 1 et sert de point de départ pour l'exo 2.

```bash
# 1. Annule toutes les modifications locales sur les fichiers suivis
git reset --hard

# 2. Supprime les fichiers non suivis
git clean -fd

# 3. Bascule sur la branche de l'exo 2
git checkout step-2-metrics
```

> ⚠️ Ces commandes **détruisent** ton travail local de l'exo 1. Si tu veux conserver ta solution, fais d'abord un `git stash` ou un commit sur une branche perso avant la réinitialisation.

## Ce que tu vas apprendre

- Configurer un **`MeterProvider`** à côté du `TracerProvider` existant via `WithMetrics()` dans la même chaîne `AddOpenTelemetry()`.
- Découvrir que les instrumenteurs déjà en place (`AddAspNetCoreInstrumentation()`, `AddHttpClientInstrumentation()`) produisent des **métriques gratuites** dès qu'un `MeterProvider` est disponible — sans toucher au code applicatif.
- Lire le format d'export d'une métrique (histogramme, temporalité, attributs) dans la console.

> On reste sur l'export **console**. Le branchement réseau vers le collecteur viendra à l'étape 4.

## Pourquoi c'est presque gratuit

Les packages `OpenTelemetry.Instrumentation.AspNetCore` et `OpenTelemetry.Instrumentation.Http` couvrent **deux signaux** : traces et métriques. À l'étape 1, seul le `TracerProvider` était configuré via `WithTracing()` — les instrumenteurs ont produit des spans, mais les métriques étaient droppées silencieusement (pas de `MeterProvider` actif).

En ajoutant simplement `.WithMetrics()`, sans rien changer d'autre dans le code applicatif, on récupère d'un coup :

- `http.server.request.duration` — durée des requêtes entrantes ASP.NET Core (histogramme),
- `http.client.request.duration` — durée des requêtes HTTP sortantes via `HttpClient` (histogramme),
- `http.server.active_requests` — nombre de requêtes en cours de traitement (jauge).

C'est la démonstration vivante qu'une API d'instrumentation bien pensée **sépare producteurs et consommateurs** : les libs produisent dès qu'un consommateur (provider) est disponible.

## Exercice

### 1. Aucun nouveau package NuGet

Pas besoin de modifier [billing-service/billing-service.csproj](../../../src/billing-service/billing-service.csproj). Le package `OpenTelemetry.Exporter.Console` installé à l'étape 1 supporte déjà les métriques — c'est le même exporter, utilisé ici pour les deux signaux.

### 2. Enrichir la configuration OTel dans Program.cs

Modifier [billing-service/Program.cs](../../../src/billing-service/Program.cs) — ajouter le `using OpenTelemetry.Metrics;` en tête de fichier et enchaîner `.WithMetrics()` après `.WithTracing()` :

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using BillingService;
using OpenTelemetry.Metrics;
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
        .AddConsoleExporter())
    .WithMetrics(b => b                        // ← nouveau
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();
```

Points d'attention :

- **Un seul `ConfigureResource()`** pour les deux signaux : `service.name` et `service.version` sont partagés entre traces et métriques. C'est ce qui permet à Grafana de corréler les deux vues pour le même service.
- **`AddAspNetCoreInstrumentation()` et `AddHttpClientInstrumentation()` apparaissent deux fois** — une fois dans `WithTracing()`, une fois dans `WithMetrics()`. Ce sont deux registrations distinctes : la première dit à l'instrumenteur de produire des spans, la seconde de produire des métriques. Les mêmes libs font les deux.
- **`AddConsoleExporter()` dans `WithMetrics()`** : le `ConsoleExporter` pour les métriques utilise un `PeriodicExportingMetricReader` implicite avec une période de **10 s** en développement. C'est différent du `ConsoleSpanExporter` pour les traces, qui est `SimpleExportProcessor` (synchrone à chaque span). D'où le décalage observable : les spans arrivent immédiatement, les métriques toutes les 10 s.

### 3. Rebuilder et redémarrer

```bash
task up
```

### 4. Générer du trafic

Plusieurs requêtes sont nécessaires : les métriques sont exportées toutes les 10 s en fenêtre agrégée — une seule requête isolée peut facilement « rater » la fenêtre d'export.

```bash
PRODUCT_ID=$(curl -s http://localhost:8080/api/products | \
  python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")

for i in {1..10}; do
  curl -s -X POST http://localhost:8080/api/billing/checkout \
    -H "Content-Type: application/json" \
    -d "{\"user_id\": \"2\", \"items\": [{\"product_id\": \"$PRODUCT_ID\", \"quantity\": 1}]}" > /dev/null
  sleep 1
done
```

### 5. Observer les métriques dans les logs

```bash
task logs -- billing-service
```

Tu vas voir **deux types** de sortie alterner :

- les **spans** imprimés à chaque requête (ce qu'on avait à l'exo 1),
- toutes les ~10 s, un bloc **métriques** avec les mesures agrégées.

Le ConsoleExporter .NET affiche chaque data point sous cette forme — un bloc par combinaison d'attributs :

```text
Instrumentation scope (Meter):
   Name: System.Net.Http
Resource associated with Metric:
   service.name: billing-service
   service.version: 1.0.0
   service.instance.id: 37c0ffaa-535a-42d0-b0d0-9ad834233192
   telemetry.sdk.name: opentelemetry
   telemetry.sdk.language: dotnet
   telemetry.sdk.version: 1.12.0

Metric Name: http.client.request.duration
(2026-05-04T15:06:05Z, 2026-05-04T15:08:16Z] http.request.method: GET network.protocol.version: 1.1 server.address: products-service server.port: 8002 url.scheme: http Histogram
Value: Sum: 0.012 Count: 2 Min: 0.005 Max: 0.007 
(-Infinity,0.01]:0
(0.01,0.02]:2
(0.02,0.05]:0
(0.05,0.1]:0
(0.1,0.2]:0
(0.2,0.5]:0
(0.5,1]:0
(1,2]:0
...
(300,+Infinity]:0

(2026-05-04T15:06:05Z, 2026-05-04T15:08:16Z] http.request.method: POST network.protocol.version: 1.1 server.address: payment-service server.port: 8003 url.scheme: http Histogram
Value: Sum: 0.269 Count: 1 Min: 0.269 Max: 0.269 
(-Infinity,0.01]:0
...
(0.2,0.5]:1
...
(300,+Infinity]:0
```

Points clés du format réel :

- **Les attributs sont sur la même ligne** que la fenêtre temporelle, suivis du type (`Histogram`).
- **Les buckets sont en secondes** (`(0.2,0.5]` = entre 200 ms et 500 ms), pas en millisecondes.
- **Un bloc par combinaison d'attributs** : `products-service` et `payment-service` sont deux data points distincts sous la même métrique.
- **`network.peer.address`** peut apparaître en plus de `server.address` : c'est l'IP résolue, utile pour distinguer plusieurs instances d'un même service.

En plus de `http.client.request.duration`, tu verras d'autres métriques automatiques émises par .NET :

```text
Metric Name: http.client.request.time_in_queue
(2026-05-04T15:06:05Z, 2026-05-04T15:08:16Z] http.request.method: GET server.address: products-service ... Histogram
Value: Sum: 0.0073923 Count: 1 Min: 0.0073923 Max: 0.0073923

Metric Name: dns.lookup.duration
(2026-05-04T15:06:05Z, 2026-05-04T15:08:16Z] dns.question.name: products-service Histogram
Value: Sum: 0.0037048 Count: 2 Min: 0.0002755 Max: 0.0034293
```

Ces métriques réseau bas niveau (`time_in_queue`, `dns.lookup.duration`) sont émises par le runtime .NET lui-même via ses `ActivitySource` internes — elles arrivent **gratuitement** dès qu'un `MeterProvider` est actif.

## Analyse : ce qu'on voit

Quatre observations clés :

1. **Les métriques ont le même `Resource` que les spans** : `service.name=billing-service`, `service.version`, `telemetry.sdk.*`. C'est exactement ce qui permettra à Grafana (étape 4) de lier la vue métriques et la vue traces pour le même service, sans ambiguïté.
2. **Plus de métriques que prévu** — en plus de `http.client.request.duration`, le runtime .NET émet automatiquement `http.client.request.time_in_queue` (temps d'attente avant envoi) et `dns.lookup.duration` (résolution DNS). Ces métriques réseau bas niveau viennent des `ActivitySource` internes de .NET — elles n'ont pas besoin d'`AddHttpClientInstrumentation()`, elles arrivent dès qu'un `MeterProvider` est actif.
3. **Histogrammes pré-agrégés en secondes** (`Sum`, `Count`, `Min`, `Max`, buckets `(-Infinity, X]`) : ce n't est pas la valeur brute de chaque requête qui est exportée — c'est agrégé sur la fenêtre d'export côté SDK. Les buckets sont en **secondes**, pas en millisecondes : `(0.2, 0.5]` correspond à 200–500 ms.
4. **`server.address` identifie le service cible** dans `http.client.request.duration` : on peut distinguer les appels vers `products-service` des appels vers `payment-service` sans lire le code — et mesurer leur latence séparément. C'est exactement le type d'attribut utile pour un SLO par dépendance.

## Critères de validation

- [ ] `billing-service` démarre sans erreur et émet toujours ses spans (on ne casse pas l'exo 1).
- [ ] Après ~10 s de trafic, un bloc de métriques apparaît dans les logs de `billing-service`.
- [ ] Le bloc contient la métrique `http.server.request.duration` avec un `count` cohérent avec le nombre de requêtes envoyées.
- [ ] Le bloc contient la métrique `http.client.request.duration` avec plusieurs data points distingués par `server.address` (`products-service`, `payment-service`).

## Pour aller plus loin

- **Personnaliser les buckets** : `WithMetrics(b => b.AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 0.01, 0.05, 0.1, 0.5, 1.0, 5.0 } }))` pour ajuster les seuils à ton SLO — les buckets par défaut couvrent 0–10 s, souvent trop larges pour du web.
- **Filtrer la cardinalité** : `AddView("*", new MetricStreamConfiguration { TagKeys = new[] { "http.route", "http.response.status_code" } })` pour retenir seulement les attributs utiles et éviter l'explosion de séries côté backend (Prometheus, VictoriaMetrics).
- **Temporalité** : les histogrammes peuvent être `Delta` (delta depuis le dernier export) ou `Cumulative` (somme depuis le démarrage). Prometheus et VictoriaMetrics préfèrent `Cumulative` ; OTLP natif et certains backends préfèrent `Delta`. Configurable via `AddConsoleExporter(o => o.Temporality = MetricReaderTemporalityPreference.Delta)`.
- **Métriques custom** : on aurait pu, en plus de la plomberie HTTP, créer un `Counter` `billing.orders.total{status=approved|declined}` pour mesurer le métier. C'est ce qu'on fera à l'étape 5 (instrumentation custom).

## Étape suivante

Une fois l'exercice validé, direction [step3-add-logs.md](step3-add-logs.md).

Sur la branche `step-3-logs`, **tous** les services ont leurs métriques (le même pattern `WithMetrics()` a été appliqué par l'animateur) — tu y trouveras l'énoncé de l'exercice 3 sur les **logs**.
