# Étape 4 — Le collecteur OpenTelemetry : brancher les backends locaux

> Branche : `step-4-collector`
> Durée estimée : 25 min
> État à l'arrivée : `billing-service` exporte désormais **en OTLP vers le collecteur** (le basculement de `AddConsoleExporter` vers `AddOtlpExporter` a été fait par l'animateur à partir de la solution de l'exo 3). Pourtant, **rien n'apparaît encore dans Grafana** à `http://localhost:8080/grafana`. On va comprendre pourquoi, corriger, puis enrichir la télémétrie via un processor `resource`.

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
- Valider la chaîne complète : `billing-service` (.NET) → collecteur → Grafana local, pour les **trois signaux** simultanément.

---

## Section .NET — Basculer de console vers OTLP dans Program.cs

> **Note : sur la branche `step-4-collector`, l'animateur a déjà effectué cette modification.** Cette section explique ce qui a été fait et pourquoi — lis-la pour comprendre, puis reproduis-la si tu veux refaire l'exercice depuis zéro.

### Ajouter le package OTLP

Le `ConsoleExporter` (step 3) est remplacé par l'exporteur OTLP. Modifier [billing-service/billing-service.csproj](../../../src/billing-service/billing-service.csproj) — remplacer `OpenTelemetry.Exporter.Console` par `OpenTelemetry.Exporter.OpenTelemetryProtocol` :

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
  <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.12.0" />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.12.0" />
</ItemGroup>
```

### Remplacer les exporters dans Program.cs

Modifier [billing-service/Program.cs](../../../src/billing-service/Program.cs) — remplacer chaque `AddConsoleExporter()` par `AddOtlpExporter()` :

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(
            serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "billing-service",
            serviceVersion: Environment.GetEnvironmentVariable("OTEL_SERVICE_VERSION") ?? "1.0.0"))
    .WithTracing(b => b
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())          // ← remplace AddConsoleExporter()
    .WithMetrics(b => b
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())          // ← remplace AddConsoleExporter()
    ;

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.ParseStateValues = true;
    o.AddOtlpExporter();             // ← remplace AddConsoleExporter()
});
```

### Variables d'environnement dans docker-compose.yml

L'exporteur OTLP lit sa configuration depuis l'environnement. Modifier [docker-compose.yml](../../../docker-compose.yml) — dans le bloc `billing-service`, ajouter :

```yaml
billing-service:
  # ...
  environment:
    - OTEL_SERVICE_NAME=billing-service
    - OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318   # ← ajout
    - OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf                # ← ajout
```

Points d'attention :

- **`OTEL_EXPORTER_OTLP_ENDPOINT`** : pointe vers le collecteur sur le port `4318` (HTTP). Le port `4317` est réservé au gRPC — on utilise HTTP ici pour rester cohérent avec les autres services de l'atelier.
- **`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`** : force le protocole HTTP+Protobuf. Sans cette variable, le SDK .NET tente gRPC par défaut, ce qui provoquerait des erreurs de connexion sur le port 4318.
- **Pas de nouveau `depends_on`** : `otel-collector` est déjà démarré avant les services applicatifs. Le SDK retente l'export en cas d'échec transitoire au démarrage.

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
    resource_to_telemetry_conversion:
      enabled: true
  otlp_http/loki:
    endpoint: http://loki:3100/otlp
```

> **Pourquoi `otlp_http/loki` et non `loki:` ?** Loki 3.x accepte nativement l'OTLP via son endpoint HTTP `/otlp/v1/logs`. L'exporter `otlphttp` du collecteur envoie du OTLP HTTP standard — aucun plugin supplémentaire n'est nécessaire. La convention de nommage OTel Collector est `type/alias` : `otlphttp` est le type d'exporter, `loki` est l'alias libre qui l'identifie dans la config. La config Loki active cette réception via `allow_structured_metadata: true`.

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

Chaque pipeline n'a **qu'un seul exporter** : `debug`, qui imprime tout sur stdout du collecteur. Les exporters `otlp/tempo`, `prometheusremotewrite/victoriametrics` et `otlp_http/loki` sont orphelins — déclarés mais jamais invoqués. C'est une erreur classique : croire qu'il suffit de *déclarer* une config OTel pour qu'elle soit active. Dans le modèle OTel Collector, une ressource n'est utilisée que si elle est **explicitement référencée** dans `service.pipelines`.

### A.1. Brancher chaque exporter dans la bonne pipeline

La correspondance signal → backend :

| Pipeline | Backend | Exporter |
|---|---|---|
| `traces` | Tempo (OTLP gRPC) | `otlp/tempo` |
| `metrics` | VictoriaMetrics (Prometheus Remote Write) | `prometheusremotewrite/victoriametrics` |
| `logs/otlp` | Loki (OTLP HTTP natif) | `otlp_http/loki` |

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
      exporters: ["debug", "otlp_http/loki"]                           # ← ajout
```

On garde `debug` pour continuer à voir ce qui transite dans le collecteur (utile pour débugger), et on ajoute le backend correspondant. Un même signal peut être envoyé à **plusieurs exporters** en parallèle (pattern **fanout**) — c'est un des intérêts majeurs du collecteur.

### A.2. Redémarrer et tester

```bash
task up
PRODUCT_ID=$(curl -s http://localhost:8080/api/products | \
  python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")

curl -X POST http://localhost:8080/api/billing/checkout \
  -H "Content-Type: application/json" \
  -d "{\"user_id\": \"2\", \"items\": [{\"product_id\": \"$PRODUCT_ID\", \"quantity\": 1}]}"
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
{ service.name = "billing-service" }
```
Tu dois voir des traces. Déplie-en une pour observer le span serveur `POST /checkout` et ses enfants HttpClient.

**Métriques** — sélectionner la datasource **VictoriaMetrics** :
```
http_server_request_duration_seconds_count{service_name="billing-service"}
```

**Logs** — sélectionner la datasource **Loki** :
```
{service_name="billing-service"}
```

---

## Partie B — Enrichir les signaux au niveau du collecteur

On va ajouter un attribut `deployment.environment = "workshop"` à **tous les signaux** au niveau du collecteur, via un processor `resource`. Pourquoi au collecteur plutôt que dans le code de `billing-service` ?

- **Un seul point de configuration** — quelle que soit la source (.NET, Go, services Python), tout ce qui transite par le collecteur reçoit l'attribut.
- **Cohérence garantie** — pas de risque qu'un service émette sans l'attribut.
- **Pas de rebuild** — on ne touche ni aux images Docker ni au code .NET.

### B.1. Déclarer le processor `resource/env`

Modifier [src/otel-collector/otelcol-config.yaml](../../../src/otel-collector/otelcol-config.yaml) — dans la section `processors`, ajouter :

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
      exporters: ["debug", "otlp_http/loki"]
```

> **L'ordre des processors compte.** `memory_limiter` reste en **premier** (il doit pouvoir dropper les signaux en surcharge **avant** qu'on perde du travail à les enrichir). `resource/env` juste après (on enrichit tout ce qui passe). `batch` en **dernier** (on groupe les signaux déjà enrichis pour l'export).

### B.3. Redémarrer et vérifier

```bash
task up
PRODUCT_ID=$(curl -s http://localhost:8080/api/products | \
  python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")

curl -X POST http://localhost:8080/api/billing/checkout \
  -H "Content-Type: application/json" \
  -d "{\"user_id\": \"2\", \"items\": [{\"product_id\": \"$PRODUCT_ID\", \"quantity\": 1}]}"
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
billing-service (.NET)    ──┐
payment-service (Python)  ──┤
products-service (Python) ──┼──OTLP HTTP (4318)──▶ otel-collector ──processors──▶ exporters ──┬──▶ debug (stdout collecteur)
users-service (Python)    ──┤                                        │                          ├──▶ otlp/tempo ──▶ Tempo (traces)
bank-service (Go)         ──┘                                        │                          ├──▶ prometheusremotewrite ──▶ VictoriaMetrics (metrics)
                                                                     │                          └──▶ otlp_http/loki ──▶ Loki (logs, OTLP HTTP)
                                                                     │
                                                                     ├─ memory_limiter (protection backpressure)
                                                                     ├─ resource/env (ajoute deployment.environment)
                                                                     └─ batch (groupage avant export)

                                                                          ↓ tous consommés par
                                                                     Grafana (http://localhost:8080/grafana)
```

Quatre observations :

1. **Les pipelines sont des fanouts**. Un span entrant est recopié vers chaque exporter listé. On garde `debug` pour le debug local en même temps qu'on pousse vers Tempo — sans doublon de config côté `billing-service`.
2. **Les processors s'appliquent dans l'ordre déclaré**. Règle de pouce : `memory_limiter` en premier, `batch` en dernier, enrichissements au milieu.
3. **L'attribution au niveau collecteur est une technique puissante**. On peut imaginer d'autres processors `resource/*` pour tagger selon la région (`cloud.region`), la version (`service.version`)… sans toucher au code de `billing-service` ni recompiler l'image Docker.
4. **Chaque signal a son backend spécialisé** : Tempo pour les traces (waterfall view, TraceQL), VictoriaMetrics pour les métriques (séries temporelles, alerting), Loki pour les logs (full-text, corrélation via trace_id). Grafana fait le lien entre les trois.

## Critères de validation

- [ ] **Partie A** : Le collecteur démarre sans erreur de connexion (`task logs -- otel-collector`).
- [ ] **Partie A** : Dans Grafana / Tempo, un `POST /api/billing/checkout` est visible comme une trace avec au moins 3 spans partageant le même `trace_id`, sur le service `billing-service`.
- [ ] **Partie A** : Dans la vue Loki, on retrouve les logs `checkout_started` ou `checkout_completed` associés à la même trace.
- [ ] **Partie A** : Dans VictoriaMetrics, la métrique `http_server_request_duration_seconds_count{service_name="billing-service"}` est présente et augmente.
- [ ] **Partie B** : Les logs `debug` du collecteur montrent `deployment.environment: Str(workshop)` sur tous les signaux.
- [ ] **Partie B** : Le filtre TraceQL `{ resource.deployment.environment = "workshop" }` ramène des traces dans Tempo.

## Pour aller plus loin

- **Autres processors utiles en prod** :
  - `resource` pour ajouter `cloud.region=eu-west-1`, `service.version=1.2.3`…
  - `filter` ou `tail_sampling` pour échantillonner les traces (garder 100 % des erreurs, 1 % du reste).
  - `attributes` pour supprimer de la PII (`http.url`) ou renommer des clés.
  - `transform` pour des modifications plus complexes via le langage OTTL.
- **gRPC vs HTTP** : le receiver du collecteur écoute les deux (`4317` gRPC, `4318` HTTP). `billing-service` utilise `http/protobuf` (via `OTEL_EXPORTER_OTLP_PROTOCOL`) — plus simple à configurer en Docker sans gérer de certificats TLS. L'exporter `otlp/tempo` utilise gRPC — pas de problème sur le réseau interne Docker Compose.

## Étape suivante

Une fois tes trois signaux visibles dans Grafana, direction [step5-custom-instrumentation.md](step5-custom-instrumentation.md).

L'exercice 5 portera sur l'**instrumentation custom** du `billing-service` : spans métier manuels, attributs riches, events OTel, et gestion fine des erreurs vs statuts métier (bank decline n'est pas une erreur technique).
