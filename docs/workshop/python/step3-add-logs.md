# Étape 3 — Logs : bridge OTel sur tous les services Python

> Branche : `step-3-logs`
> Durée estimée : 30 min
> État à l'arrivée : tous les services Python sont instrumentés pour les **traces** (exo 1) et les **métriques** (exo 2), tout sort sur la **console**. Le `bank-service` (Go) émet aussi les trois signaux.

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

- Les **trois approches** possibles pour gérer les logs avec OpenTelemetry, et leurs compromis.
- Mettre en place le **bridge OTel complet** sur les trois services Python avec `LoggerProvider` + `LoggingHandler` + `ConsoleLogExporter` — les logs deviennent une vraie télémétrie OTel.
- Comprendre pourquoi le bridge est l'approche préférable pour un atelier où l'on veut des `LogRecord` structurés exportables via OTLP.

## Les trois approches possibles

| Approche | Ce qui change | Ce qui ne change pas |
| --- | --- | --- |
| **Corrélation seule** | Le formatter Python ajoute `trace_id`/`span_id` à chaque record | Les logs continuent de sortir via stdout/stderr, pipeline Python standard |
| **Bridge OTel seul** | Un `LoggingHandler` envoie les records vers un `LoggerProvider` OTel | Pas d'info de trace dans les records (sauf si on les corrèle aussi) |
| **Bridge + corrélation** (approche choisie ici) | Les deux : records enrichis **et** exportés via OTLP | — |

La **corrélation seule** est souvent suffisante si tu gardes ton pipeline de logs existant (ELK, Loki via Promtail…). Le **bridge complet** unifie toute la télémétrie dans une seule pipeline OTel et préserve les `extra={...}` comme attributs structurés indexables. C'est l'approche choisie ici pour les trois services.

---

## Exercice — Bridge OTel sur `payment-service`, `products-service` et `users-service`

Le même pattern s'applique aux trois services. L'exercice porte sur `payment-service` ; `products-service` et `users-service` reçoivent la même transformation.

### 1. Ajouter la dépendance

Modifier `requirements.txt` de chaque service — ajouter **une** ligne :

```text
opentelemetry-instrumentation-logging==0.50b0
```

> Le `ConsoleLogExporter` et le `LoggerProvider` sont dans `opentelemetry-sdk` déjà présent — pas de nouveau paquet SDK.

### 2. Ajouter `setup_logging()` à `otel.py`

Modifier `otel.py` de chaque service — enrichir les imports OTel existants et ajouter la fonction :

```python
import logging
import os

from opentelemetry import _logs, metrics, trace
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor, ConsoleLogExporter
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import ConsoleMetricExporter, PeriodicExportingMetricReader
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import ConsoleSpanExporter, SimpleSpanProcessor


def _resource() -> Resource:
    return Resource.create({
        "service.name": os.getenv("OTEL_SERVICE_NAME", "payment-service"),  # adapter par service
        "service.version": os.getenv("OTEL_SERVICE_VERSION", "1.0.0"),
    })


def setup_tracing() -> None:
    provider = TracerProvider(resource=_resource())
    provider.add_span_processor(SimpleSpanProcessor(ConsoleSpanExporter()))
    trace.set_tracer_provider(provider)


def setup_metrics() -> None:
    reader = PeriodicExportingMetricReader(
        ConsoleMetricExporter(),
        export_interval_millis=10_000,
    )
    provider = MeterProvider(resource=_resource(), metric_readers=[reader])
    metrics.set_meter_provider(provider)


def setup_logging() -> None:
    provider = LoggerProvider(resource=_resource())
    provider.add_log_record_processor(BatchLogRecordProcessor(ConsoleLogExporter()))
    _logs.set_logger_provider(provider)

    handler = LoggingHandler(level=logging.INFO, logger_provider=provider)
    root = logging.getLogger()
    root.setLevel(logging.NOTSET)
    root.addHandler(handler)
```

> ⚠️ **Pourquoi `_logs` avec un underscore, et pas `logs` ?**
>
> L'API des logs dans `opentelemetry-api` est encore marquée expérimentale. L'underscore est le marqueur idiomatique Python pour « API non publique — peut changer sans garantie de compatibilité ». Même logique côté SDK : `opentelemetry.sdk._logs` est préfixé. Le jour où l'API est stabilisée (comme l'a été metrics), les imports perdront leur underscore.

Quatre pièces à noter :

- **`LoggerProvider(resource=_resource())`** : même `Resource` que les traces et métriques. Un log aura donc le bon `service.name` quand il arrive dans le backend.
- **`BatchLogRecordProcessor`** : équivalent du `BatchSpanProcessor` pour les logs — bufferise et exporte par paquet.
- **`root.setLevel(logging.NOTSET)`** : le root logger Python a un niveau par défaut `WARNING`. Sans cette ligne, les records `INFO` émis par les loggers enfants (`logging.getLogger("products")`, etc.) sont filtrés **au niveau du logger** avant d'atteindre le handler — le `LoggingHandler` ne les voit jamais. `NOTSET` (= 0) supprime ce filtre au niveau du root logger ; c'est ensuite le `LoggingHandler(level=logging.INFO)` qui contrôle ce qu'il exporte.
- **`root.addHandler(handler)`** : on **ajoute** un handler au root logger. Les handlers existants (ceux qui écrivent sur stdout) ne sont pas remplacés. Résultat : chaque log est émis **deux fois**, une en format texte Python (console classique), une en format OTel LogRecord (console via OTel).

### 3. Brancher les instrumenteurs dans `app.py`

Modifier `app.py` de chaque service — ajouter les imports et les appels **avant** la création de l'application Flask :

```python
from opentelemetry.instrumentation.flask import FlaskInstrumentor
from opentelemetry.instrumentation.logging import LoggingInstrumentor   # ← ajouter
from opentelemetry.instrumentation.sqlite3 import SQLite3Instrumentor

from otel import setup_tracing, setup_metrics, setup_logging   # ← setup_logging

setup_tracing()
setup_metrics()
setup_logging()                                                # ← ajouter
SQLite3Instrumentor().instrument()
LoggingInstrumentor().instrument(set_logging_format=True)      # ← ajouter
```

> **Ordre d'initialisation** : `setup_logging()` **avant** `LoggingInstrumentor().instrument()`. Le `LoggingHandler` enregistré par `setup_logging()` doit être en place avant que l'instrumenteur patche la `LogRecord` factory.

Pour `payment-service`, ajouter aussi `GrpcInstrumentorClient().instrument()` entre `setup_logging()` et `SQLite3Instrumentor().instrument()`.

### 4. Rebuilder et redémarrer

```bash
task build:payment-service
task build:products-service
task build:users-service
task up
```

### 5. Générer du trafic

```bash
curl http://localhost:8002/products
```

### 6. Observer la double sortie

```bash
task logs -- products-service
```

Tu vas voir **deux formats** arriver pour chaque log :

1. La ligne Python classique avec corrélation (grâce à `set_logging_format=True`) :

   ```text
   2026-04-19 12:35:40,123 INFO [products] [app.py:33] [trace_id=9b2e... span_id=7a1c... resource.service.name=products-service trace_sampled=True] - products_listed
   ```

2. Le **LogRecord OTel** (format JSON-ish) exporté par le `ConsoleLogExporter` :

   ```text
   {
       "body": "products_listed",
       "severity_number": "<SeverityNumber.INFO: 9>",
       "severity_text": "INFO",
       "attributes": {
           "status_filter": "Active",
           "result_count": 42
       },
       "timestamp": "2026-04-19T12:35:40.123456Z",
       "trace_id": "0x9b2e...",
       "span_id": "0x7a1c...",
       "trace_flags": 1,
       "resource": {
           "attributes": {
               "service.name": "products-service",
               "service.version": "1.0.0"
           }
       }
   }
   ```

Le second format est une **vraie télémétrie structurée** : `body`, `severity_number`, `attributes` (y compris les `extra={...}` passés au logger), `timestamp`, `trace_id`/`span_id`, et `resource` — tout ce qu'il faut pour un backend d'observabilité moderne.

---

## Analyse comparative : bridge vs corrélation seule

| Aspect | Corrélation seule | Bridge + corrélation (approche choisie) |
| --- | --- | --- |
| `otel.py` | Intact | Nouveau `setup_logging()` + imports logs |
| `requirements.txt` | +1 paquet | +1 paquet (même paquet) |
| Format sortie | Ligne texte Python enrichie | Ligne texte **ET** LogRecord JSON |
| Attributs structurés (`extra={...}`) | Non (perdus dans le message texte) | Oui (préservés dans `attributes`) |
| Export distant possible | Non | Oui (remplace `ConsoleLogExporter` par `OTLPLogExporter`) |

## Critères de validation

- [ ] `payment-service`, `products-service` et `users-service` démarrent sans erreur.
- [ ] Un `GET /products` produit **deux** sorties par log : une ligne Python ET un LogRecord JSON.
- [ ] Le LogRecord JSON contient `body`, `severity_text`, `resource.attributes.service.name`, et un `trace_id`/`span_id` non nul.
- [ ] Les `extra={status_filter: ..., result_count: ...}` sont visibles dans les `attributes` du LogRecord.

## Pour aller plus loin

- **Format custom** : `LoggingInstrumentor().instrument(set_logging_format=False)` si tu veux garder ton propre format. Dans ce cas, tu ajoutes toi-même `%(otelTraceID)s` / `%(otelSpanID)s` dans ton format string.
- **Corrélation inter-services** : comme les `trace_id` sont propagés en amont par les instrumenteurs, un log émis dans `bank-service` porte déjà **le même** `trace_id` que le log dans `payment-service` pour une requête donnée. Dans un backend comme Grafana Loki, on peut faire `{trace_id="feeac4..."}` pour sortir tous les logs de la trace, **cross-service**.
- **Pourquoi `_logs` et pas `logs` ?** L'API stable pour les logs dans `opentelemetry-api` est encore estampillée "expérimentale" dans certaines versions : d'où les imports `opentelemetry._logs` (underscore) et `opentelemetry.sdk._logs`. C'est le signal que l'API peut évoluer.
- **`BatchLogRecordProcessor` vs `SimpleLogRecordProcessor`** : on a choisi le batch pour cohérence avec la pratique (moins de pression I/O). Pour une console pédagogique, `SimpleLogRecordProcessor` donne un retour immédiat — à essayer si la latence de 1–2 s te gêne.

## Étape suivante

Une fois les trois services validés, direction [step4-add-collector.md](step4-add-collector.md).

Sur la branche `step-4-collector` :

- Les trois services Python gardent le bridge complet.
- `billing-service` (.NET) a également reçu le bridge logging OTel (fait par l'animateur).
- L'exercice 4 portera sur la mise en place du **collecteur OpenTelemetry** pour enfin sortir les trois signaux de la console et les envoyer vers Grafana.
