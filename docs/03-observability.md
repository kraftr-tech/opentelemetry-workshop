# Observabilité

Vue d'ensemble de la stack OpenTelemetry utilisée par l'atelier : où naissent les signaux, par où ils transitent, où ils sont stockés et visualisés.

## Pipeline des signaux

```text
┌────────────────────────┐       OTLP         ┌────────────────┐      ┌──▶ Tempo (traces)
│  Services applicatifs  │ ─────────────────▶ │ OTel Collector │ ─────┼──▶ VictoriaMetrics (métriques)
│  (traces, metrics,     │                    │                │      └──▶ Loki (logs)
│   logs)                │                    │                │
└────────────────────────┘                    └────────────────┘
                                                                    ↓ visualisés via
                                                               Grafana (http://localhost:8080/grafana)
```

## Instrumentation par service

### Services Python (`users`, `products`, `payment`)

Instrumentation via le SDK OpenTelemetry Python — activée progressivement au fil du workshop. L'auto-instrumentation Flask, requests, SQLite3, gRPC client est disponible via les packages `opentelemetry-instrumentation-*`.

### Service .NET (`billing-service`)

**Instrumentation manuelle** avec le SDK OTel .NET — à implémenter dans les étapes suivantes du workshop. Points d'accroche prévus :

| Fichier                | Instrumentation future                                    |
| ---------------------- | --------------------------------------------------------- |
| `Program.cs`           | Traces HTTP entrantes (`AddAspNetCoreInstrumentation`)    |
| `BillingService.cs`    | Spans custom sur la logique checkout                      |
| `BillingRepository.cs` | Spans DB (`AddSqlClientInstrumentation` ou manuel)        |
| HttpClient (via factory) | Traces HTTP sortantes (`AddHttpClientInstrumentation`) |

### Service Go (`bank-service`)

**Instrumentation manuelle** avec le SDK OTel Go — voir [bank-service/otel.go](../bank-service/otel.go). Les spans custom vivent dans [bank-service/service.go](../bank-service/service.go). Le serveur gRPC utilise l'intercepteur `otelgrpc`.

## Configuration du Collecteur

Deux chemins possibles selon le mode d'exécution :

### Docker Compose

Le collecteur utilise [configs/otelcol-config.yaml](../configs/otelcol-config.yaml). Pipeline actuel :

- **Receivers** : OTLP gRPC (`:4317`) + OTLP HTTP (`:4318`).
- **Processors** : `memory_limiter` → `batch`.
- **Exporters** : `debug` (stdout) + `otlp/tempo` (traces) + `prometheusremotewrite/victoriametrics` (métriques) + `loki` (logs).


## Ce qui circule sur chaque signal

| Signal    | Source                                                  | Backend de stockage                     |
| --------- | ------------------------------------------------------- | --------------------------------------- |
| Traces    | Flask SDK + .NET SDK (billing) + Go `otelgrpc` + spans custom (bank) | Tempo                       |
| Metrics   | Flask SDK + .NET SDK (billing) + Go metric SDK                       | VictoriaMetrics             |
| Logs      | Python logging bridge + .NET SDK (billing) + Go `slog` bridge        | Loki                        |

> L'état exact des signaux exportés **dépend de la branche du workshop** : chaque étape ajoute progressivement un signal ou une cible. Voir [workshop/README.md](workshop/README.md).

## Voir les données

- **Logs collecteur** : le collecteur est en `verbosity: detailed` sur l'exporter `debug`. Tail les logs :

  ```bash
  task logs
  ```

- **Grafana** : ouvrir <http://localhost:8080/grafana> → Explore → Tempo / VictoriaMetrics / Loki avec le filtre `service.name`.

## Référence

- [Spec Auto-instrumentation Python](https://opentelemetry.io/docs/zero-code/python/)
- [OTel Collector Contrib](https://github.com/open-telemetry/opentelemetry-collector-contrib)
