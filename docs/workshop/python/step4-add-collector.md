# Étape 4 — Le collecteur OpenTelemetry : brancher les backends locaux

> Branche : `step-4-collector`
> Durée estimée : 25 min
> État à l'arrivée : tous les services exportent désormais **en OTLP vers le collecteur** (les `ConsoleXxxExporter` ont été remplacés par les `OTLPXxxExporter` — travail de l'animateur sur la base de l'exo 3). Pourtant, **rien n'apparaît encore dans Grafana** à `http://localhost:8080/grafana`. On va comprendre pourquoi, corriger, puis enrichir la télémétrie via un processor `resource`.

---

## Section Python & Go — Basculer de console vers OTLP (travail de l'animateur)

> **Note : sur la branche `step-4-collector`, l'animateur a déjà effectué ces modifications.** Cette section documente ce qui a été fait et pourquoi — lis-la pour comprendre, puis reproduis-la si tu veux refaire l'exercice depuis zéro.

### Services Python (`payment-service`, `products-service`, `users-service`)

#### 1. Ajouter le paquet OTLP dans `requirements.txt`

```text
opentelemetry-exporter-otlp-proto-http==1.29.0
```

#### 2. Réécrire `otel.py`

Les `ConsoleXxxExporter` sont remplacés par leurs équivalents OTLP HTTP. Le `SimpleSpanProcessor` passe en `BatchSpanProcessor` (approprié pour un export réseau). Une ligne supplémentaire configure le niveau du root logger Python :

```python
import logging
import os

from opentelemetry import _logs, metrics, trace
from opentelemetry.exporter.otlp.proto.http._log_exporter import OTLPLogExporter
from opentelemetry.exporter.otlp.proto.http.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor


def _resource() -> Resource:
    return Resource.create({
        "service.name": os.getenv("OTEL_SERVICE_NAME", "payment-service"),  # adapter par service
        "service.version": os.getenv("OTEL_SERVICE_VERSION", "1.0.0"),
    })


def setup_tracing() -> None:
    provider = TracerProvider(resource=_resource())
    provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
    trace.set_tracer_provider(provider)


def setup_metrics() -> None:
    reader = PeriodicExportingMetricReader(
        OTLPMetricExporter(),
        export_interval_millis=10_000,
    )
    provider = MeterProvider(resource=_resource(), metric_readers=[reader])
    metrics.set_meter_provider(provider)


def setup_logging() -> None:
    provider = LoggerProvider(resource=_resource())
    provider.add_log_record_processor(BatchLogRecordProcessor(OTLPLogExporter()))
    _logs.set_logger_provider(provider)

    handler = LoggingHandler(level=logging.INFO, logger_provider=provider)
    root = logging.getLogger()
    root.setLevel(logging.NOTSET)   # ← indispensable (voir note ci-dessous)
    root.addHandler(handler)
```

> **Pourquoi `root.setLevel(logging.NOTSET)` ?**
>
> Le root logger Python a un niveau par défaut `WARNING`. Quand gunicorn démarre les workers, il ne modifie pas ce niveau. Résultat : les appels `logger.info(...)` dans le code applicatif sont filtrés **avant** d'atteindre le `LoggingHandler` — aucun record n'arrive dans l'exporteur OTLP.
>
> `NOTSET` (= 0) supprime le filtre au niveau du root logger : tous les records passent jusqu'aux handlers, et c'est le `LoggingHandler(level=logging.INFO)` qui décide ce qu'il exporte vers le collecteur.

#### 3. Variables d'environnement dans `docker-compose.yml`

Pour chaque service Python, ajouter dans `environment` :

```yaml
- OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
- OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

Et dans `depends_on` :

```yaml
- otel-collector
```

> **`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`** : non strictement nécessaire pour les classes Python (leur module `.proto.http` est déjà câblé HTTP), mais documenté pour signaler explicitement le protocole utilisé.

---

### `bank-service` (Go)

#### 1. Ajouter les métriques et le bridge logs dans `otel.go`

```go
import (
    "go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp"
    "go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetrichttp"
    "go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracehttp"
    "go.opentelemetry.io/otel/log/global"
    sdklog "go.opentelemetry.io/otel/sdk/log"
    sdkmetric "go.opentelemetry.io/otel/sdk/metric"
    // ... autres imports existants
)

func initOtel(ctx context.Context) func() {
    // Traces — inchangé, stdouttrace remplacé par otlptracehttp
    traceExporter, _ := otlptracehttp.New(ctx)
    tp := sdktrace.NewTracerProvider(
        sdktrace.WithSpanProcessor(sdktrace.NewBatchSpanProcessor(traceExporter)),
        sdktrace.WithResource(res),
    )
    otel.SetTracerProvider(tp)
    otel.SetTextMapPropagator(...)

    // Métriques — nouveau
    metricExporter, _ := otlpmetrichttp.New(ctx)
    mp := sdkmetric.NewMeterProvider(
        sdkmetric.WithReader(sdkmetric.NewPeriodicReader(metricExporter,
            sdkmetric.WithInterval(10*time.Second),
        )),
        sdkmetric.WithResource(res),
    )
    otel.SetMeterProvider(mp)

    // Logs — nouveau
    logExporter, _ := otlploghttp.New(ctx)
    lp := sdklog.NewLoggerProvider(
        sdklog.WithProcessor(sdklog.NewBatchProcessor(logExporter)),
        sdklog.WithResource(res),
    )
    global.SetLoggerProvider(lp)

    return func() {
        // shutdown tp, mp, lp
    }
}
```

Modules Go requis (à ajouter via `go get`) :
- `go.opentelemetry.io/otel/log v0.19.0`
- `go.opentelemetry.io/otel/sdk/log v0.19.0`
- `go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploghttp v0.19.0`
- `go.opentelemetry.io/otel/exporters/otlp/otlpmetric/otlpmetrichttp v1.43.0`
- `go.opentelemetry.io/contrib/bridges/otelslog v0.18.0`

#### 2. Brancher `slog` vers OTel dans `logger.go`

`otelslog.NewHandler("bank-service")` lit le `LoggerProvider` global de façon **paresseuse** (au moment du log, pas à la création). On peut donc créer le handler avant `initOtel()` — les logs émis avant l'initialisation vont uniquement sur stderr, les suivants vont sur les deux.

Un `fanoutHandler` combine la sortie stderr (visible dans `docker logs`) et le bridge OTel (visible dans Loki) :

```go
import "go.opentelemetry.io/contrib/bridges/otelslog"

func setupLogger() {
    logger = slog.New(&fanoutHandler{handlers: []slog.Handler{
        slog.NewTextHandler(os.Stderr, nil),
        otelslog.NewHandler("bank-service"),
    }})
}
```

#### 3. Variable d'environnement dans `docker-compose.yml`

```yaml
bank-service:
  environment:
    - OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
```

> `OTEL_EXPORTER_OTLP_PROTOCOL` n'est pas nécessaire pour Go : `otlptracehttp`, `otlpmetrichttp` et `otlploghttp` sont des packages HTTP par définition — le protocole est câblé par le choix du package.

---

## Préparation : passer sur la branche `step-4-collector`

Tu arrives de l'exo 3 avec des modifications locales sur `step-3-logs`. On les **écarte** pour récupérer l'état propre de `step-4-collector` (solution de référence de l'exo 3 + bascule OTLP + point de départ de l'exo 4).

```bash
# 1. Annule toutes les modifications locales sur les fichiers suivis
git reset --hard

# 2. Supprime les fichiers non suivis
git clean -fd

# 3. Bascule sur la branche de l'exo 4
git checkout step-4-collector
```

> ⚠️ Ces commandes **détruisent** ton travail local de l'exo 3. Si tu veux conserver ta solution, fais d'abord un `git stash` ou un commit sur une branche perso avant la réinitialisation.

## Ce que tu vas apprendre

- L'anatomie d'une config OTel Collector : **receivers → processors → exporters → pipelines**.
- La différence entre **déclarer un exporter** et le **brancher dans une pipeline** (c'est là où le bât blesse aujourd'hui).
- Utiliser un processor **`resource`** pour enrichir toute la télémétrie avec un attribut custom, au niveau du collecteur plutôt qu'au niveau du code.
- Valider la chaîne complète : service Python → collecteur → Grafana local, pour les **trois signaux** simultanément.

---

## Partie A — Brancher les backends locaux

### Pourquoi rien n'arrive dans Grafana ?

Ouvre [src/otel-collector/otelcol-config.yaml](../../../src/otel-collector/otelcol-config.yaml). Regarde la section `exporters` :

```yaml
exporters:
  debug:
    verbosity: detailed
  otlp/tempo:
    endpoint: http://tempo:4317
    tls:
      insecure: true
  prometheusremotewrite/victoriametrics:
    endpoint: http://victoriametrics:8428/api/v1/write
  loki:
    endpoint: http://loki:3100/loki/api/v1/push
```

Les trois exporters vers les backends locaux sont **bien déclarés**. Tempo pour les traces, VictoriaMetrics pour les métriques, Loki pour les logs. Mais regarde maintenant la section `service.pipelines` :

```yaml
service:
  pipelines:
    traces:
      receivers: ["otlp"]
      processors: ["memory_limiter", "batch"]
      exporters: ["debug"]
    metrics:
      receivers: ["otlp"]
      processors: ["memory_limiter", "batch"]
      exporters: ["debug"]
    logs/otlp:
      receivers: ["otlp"]
      processors: ["memory_limiter", "batch"]
      exporters: ["debug"]
```

Chaque pipeline n'a **qu'un seul exporter** : `debug`, qui imprime tout sur stdout du collecteur. Les exporters `otlp/tempo`, `prometheusremotewrite/victoriametrics` et `loki` sont orphelins — déclarés mais jamais invoqués. C'est une erreur classique : croire qu'il suffit de *déclarer* une config OTel pour qu'elle soit active. Dans le modèle OTel Collector, une ressource n'est utilisée que si elle est **explicitement référencée** dans `service.pipelines`.

### A.1. Brancher chaque exporter dans la bonne pipeline

La correspondance signal → backend :

| Pipeline | Backend | Exporter |
|---|---|---|
| `traces` | Tempo (stockage traces, OTLP gRPC) | `otlp/tempo` |
| `metrics` | VictoriaMetrics (compatible Prometheus Remote Write) | `prometheusremotewrite/victoriametrics` |
| `logs/otlp` | Loki (ingest logs via API HTTP) | `loki` |

Modifier [src/otel-collector/otelcol-config.yaml](../../../src/otel-collector/otelcol-config.yaml) — pour **chacune** des trois pipelines, ajouter l'exporter correspondant :

```yaml
service:
  pipelines:
    traces:
      receivers: ["otlp"]
      processors: ["memory_limiter", "batch"]
      exporters: ["debug", "otlp/tempo"]                              # ← ajout
    metrics:
      receivers: ["otlp"]
      processors: ["memory_limiter", "batch"]
      exporters: ["debug", "prometheusremotewrite/victoriametrics"]   # ← ajout
    logs/otlp:
      receivers: ["otlp"]
      processors: ["memory_limiter", "batch"]
      exporters: ["debug", "loki"]                                    # ← ajout
```

On garde `debug` pour continuer à voir ce qui transite dans le collecteur (utile pour débugger), et on ajoute le backend correspondant pour persister les données. Un même signal peut être envoyé à **plusieurs exporters** en parallèle (pattern **fanout**) — c'est un des intérêts majeurs du collecteur.

### A.2. Redémarrer et tester

```bash
task up
curl -X POST http://localhost:8003/payments \
  -H "Content-Type: application/json" \
  -d '{"user_id": "user-42", "amount": 49.90}'
```

Dans les logs du collecteur :

```bash
task logs -- otel-collector
```

Attendu : spans imprimés par l'exporter `debug`, **pas d'erreur de connexion** (`failed to export`, `connection refused`), pas de ligne `exporterhelper` avec `dropped items`.

### A.3. Vérifier dans Grafana

Ouvrir **<http://localhost:8080/grafana>** → Explore.

**Traces** — sélectionner la datasource **Tempo** :
```
{ service.name = "payment-service" }
```
Tu dois voir des traces. Déplie-en une pour observer l'arbre de spans `payment-service` → `bank-service`.

**Métriques** — sélectionner la datasource **VictoriaMetrics** :
```
http_server_request_duration_seconds_count{service_name="payment-service"}
```

**Logs** — sélectionner la datasource **Loki** :
```
{service_name="payment-service"}
```

---

## Partie B — Enrichir les signaux au niveau du collecteur

On va ajouter un attribut `deployment.environment = "workshop"` à **tous les signaux** au niveau du collecteur, via un processor `resource`. Pourquoi au collecteur plutôt que dans le code des services ?

- **Un seul point de configuration** — quelle que soit la source (Python, Go, ajouts futurs), tout ce qui transite par le collecteur reçoit l'attribut.
- **Cohérence garantie** — pas de risque qu'un service émette sans l'attribut.
- **Pas de rebuild** — on ne touche ni aux images Docker ni au code.

### B.1. Déclarer le processor `resource/env`

Modifier [src/otel-collector/otelcol-config.yaml](../../../src/otel-collector/otelcol-config.yaml) — dans la section `processors`, ajouter une nouvelle entrée :

```yaml
processors:
  batch:
    timeout: 5s
  memory_limiter:
    check_interval: 5s
    limit_percentage: 80
    spike_limit_percentage: 25
  resource/env:                          # ← nouveau
    attributes:
      - key: deployment.environment
        value: "workshop"
        action: upsert
```

Points à noter :

- **`resource/env`** : le nom utilise le préfixe de type (`resource`) suivi d'un **alias** libre (`env`) — convention OTel pour pouvoir configurer plusieurs instances du même processor.
- **`action: upsert`** : ajoute l'attribut s'il n'existe pas, le remplace s'il existe. Alternatives : `insert` (n'écrase pas), `update` (ne crée pas), `delete`.

### B.2. Brancher le processor dans les trois pipelines

Ajouter `resource/env` à la liste `processors` de chaque pipeline :

```yaml
service:
  pipelines:
    traces:
      receivers: ["otlp"]
      processors: ["memory_limiter", "resource/env", "batch"]   # ← ajout
      exporters: ["debug", "otlp/tempo"]
    metrics:
      receivers: ["otlp"]
      processors: ["memory_limiter", "resource/env", "batch"]   # ← ajout
      exporters: ["debug", "prometheusremotewrite/victoriametrics"]
    logs/otlp:
      receivers: ["otlp"]
      processors: ["memory_limiter", "resource/env", "batch"]   # ← ajout
      exporters: ["debug", "loki"]
```

> **L'ordre des processors compte.** `memory_limiter` reste en **premier** (il doit pouvoir dropper les signaux en surcharge **avant** qu'on perde du travail à les enrichir). `resource/env` juste après (on enrichit tout ce qui passe). `batch` en **dernier** (on groupe les signaux déjà enrichis pour l'export).

### B.3. Redémarrer et vérifier

```bash
task up
curl -X POST http://localhost:8003/payments \
  -H "Content-Type: application/json" \
  -d '{"user_id": "user-42", "amount": 49.90}'
```

Côté logs du collecteur (exporter `debug`) :

```bash
task logs -- otel-collector | grep -A1 "deployment.environment"
```

Tu dois voir `deployment.environment: Str(workshop)` dans les `Resource attributes` des signaux.

### B.4. Filtrer dans Grafana

Note : les noms d'attributs avec des points sont normalisés différemment selon le backend :

| Backend | Convention | Requête exemple |
| --- | --- | --- |
| **Tempo** (traces) | attribut conservé tel quel | `{ resource.deployment.environment = "workshop" }` (TraceQL) |
| **VictoriaMetrics** (metrics) | `.` → `_` | `http_server_request_duration_seconds_count{deployment_environment="workshop"}` |
| **Loki** (logs) | `.` → `_` | `{deployment_environment="workshop"}` |

---

## Analyse : la chaîne complète

```text
Python services ──OTLP HTTP──▶ otel-collector ──processors──▶ exporters ──┬──▶ debug (stdout collecteur)
                                               ↑                          ├──▶ otlp/tempo ──▶ Tempo (traces)
bank-service (Go) ──OTLP HTTP──┘               │                          ├──▶ prometheusremotewrite ──▶ VictoriaMetrics (metrics)
                                               │                          └──▶ loki ──▶ Loki (logs)
                                               │
                                               ├─ memory_limiter (protection backpressure)
                                               ├─ resource/env (ajoute deployment.environment)
                                               └─ batch (groupage avant export)

                                                    ↓ tous les trois consommés par
                                               Grafana (http://localhost:8080/grafana)
```

Quatre observations :

1. **Les pipelines sont des fanouts**. Un span entrant est recopié vers chaque exporter listé. On garde `debug` pour le debug local en même temps qu'on pousse vers Tempo — sans doublon de config côté services.
2. **Les processors s'appliquent dans l'ordre déclaré**. Inverser `resource/env` et `batch` ne changerait rien ici (batch ne touche pas les attributs), mais inverser `memory_limiter` avec le reste casserait la protection. Règle de pouce : `memory_limiter` en premier, `batch` en dernier, enrichissements au milieu.
3. **L'attribution au niveau collecteur est une technique puissante**. On peut imaginer d'autres processors `resource/*` pour tagger selon la région (`cloud.region`), la version (`service.version`)… sans toucher au code des services.
4. **Chaque signal a son backend spécialisé** : Tempo pour les traces (recherche par trace_id, waterfall view), VictoriaMetrics pour les métriques (séries temporelles, alerting), Loki pour les logs (full-text, corrélation via trace_id). Grafana fait le lien entre les trois.

## Critères de validation

- [ ] **Partie A** : Le collecteur démarre sans erreur de connexion (`task logs -- otel-collector`).
- [ ] **Partie A** : Dans Grafana / Tempo, un `POST /payments` est visible comme une trace avec au moins 4 spans partageant le même `trace_id`, sur les services `payment-service` et `bank-service`.
- [ ] **Partie A** : Dans la vue Loki, on retrouve les logs `payment_approved` ou `checkout_started` associés à la même trace.
- [ ] **Partie A** : Dans VictoriaMetrics, la métrique `http_server_request_duration_seconds_count{service_name="payment-service"}` est présente et augmente.
- [ ] **Partie B** : Les logs `debug` du collecteur montrent `deployment.environment: Str(workshop)` sur tous les signaux.
- [ ] **Partie B** : Le filtre TraceQL `{ resource.deployment.environment = "workshop" }` ramène des traces dans Tempo.

## Pour aller plus loin

- **Autres processors utiles en prod** :
  - `resource` pour ajouter `cloud.region=eu-west-1`, `service.version=1.2.3`…
  - `filter` ou `tail_sampling` pour échantillonner les traces (garder 100 % des erreurs, 1 % du reste).
  - `attributes` pour supprimer de la PII (`http.url`) ou renommer des clés.
  - `transform` pour des modifications plus complexes via le langage OTTL.
- **gRPC vs HTTP** : le receiver du collecteur écoute les deux (`4317` gRPC, `4318` HTTP). Nos services Python et Go utilisent `http/protobuf` — plus simple à débugger. Les exporters locaux (`otlp/tempo`) utilisent gRPC — pas de problème car le réseau est interne au Docker Compose.

## Étape suivante

Une fois tes trois signaux visibles dans Grafana, direction [step5-custom-metrics.md](step5-custom-metrics.md).

L'exercice 5 portera sur l'**instrumentation custom** du `payment-service` : spans métier manuels, attributs riches, events, et gestion fine des erreurs vs statuts métier (bank decline n'est pas une erreur technique).
