# Step 0 — Auto-instrumentation zero-code (`billing-service`)

> Branche : `main`
> Durée estimée : 10 min

## Ce que tu vas apprendre

- Activer l'**auto-instrumentation zero-code** sur un service .NET via le package NuGet `OpenTelemetry.AutoInstrumentation`.
- Comprendre ce qu'elle instrumente automatiquement : spans serveur ASP.NET Core, spans HttpClient, propagation W3C Trace Context.
- Identifier ses **limites** : pas de spans custom, pas d'attributs métier, pas de métriques applicatives.
- Comprendre pourquoi les steps suivants passent à l'approche **programmatique** pour une observabilité complète.

## Pourquoi commencer par là ?

Le concept d'auto-instrumentation zero-code existe pour la plupart des runtimes : une configuration externe, zéro ligne de code, des spans serveur et client générés automatiquement. Pratique — mais opaque, limité à ce que les instrumenteurs supportent, et sans attributs métier.

Pour .NET, le mécanisme repose sur le package NuGet **`OpenTelemetry.AutoInstrumentation`**. Quand il est référencé dans le projet, il dépose dans le répertoire de build :

- `instrument.sh` (Linux) et `instrument.cmd` (Windows) — scripts de démarrage qui positionnent les variables d'environnement du profiler CLR.
- Les libraries du profiler CLR (`OpenTelemetry.AutoInstrumentation.Native.so`, etc.) qui s'accrochent au runtime .NET.
- Les assemblies d'instrumentation (ASP.NET Core, HttpClient, SqlClient, gRPC…) chargées automatiquement.

Le mécanisme repose sur le **CLR Profiling API** : en positionnant `CORECLR_ENABLE_PROFILING=1`, `CORECLR_PROFILER` et `CORECLR_PROFILER_PATH`, on demande au runtime d'injecter du code d'instrumentation dans les classes cibles — sans modifier le code source. C'est pourquoi l'ENTRYPOINT doit passer par `instrument.sh` : c'est lui qui configure ces variables avant de lancer `dotnet`.

Cette étape est une **démonstration** : elle montre ce qu'on obtient gratuitement, avant de montrer pourquoi l'approche programmatique (step 1 et suivants) est nécessaire pour une observabilité métier complète.

## Exercice

### 1. Ajouter le package NuGet

Modifier [billing-service.csproj](../../../src/billing-service/billing-service.csproj) — ajouter le package dans le bloc `<ItemGroup>` :

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>BillingService</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
    <PackageReference Include="OpenFeature.Contrib.Providers.Flagd" Version="0.5.*" />
    <PackageReference Include="OpenTelemetry.AutoInstrumentation" Version="1.15.0" />
  </ItemGroup>
</Project>
```

> **Ce que le package dépose dans le répertoire de build** : `instrument.sh`, `instrument.cmd`, les libraries du profiler CLR (`OpenTelemetry.AutoInstrumentation.Native.so`), et les assemblies d'instrumentation (ASP.NET Core, HttpClient, gRPC, SqlClient…). Aucune modification du code source n'est nécessaire — tout passe par le profiler CLR.

### 2. Modifier le Dockerfile pour utiliser `instrument.sh`

Modifier **uniquement l'`ENTRYPOINT`** dans [Dockerfile](../../../src/billing-service/Dockerfile) :

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY src/billing-service/billing-service.csproj .
RUN dotnet restore
COPY src/billing-service/ .
RUN dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
EXPOSE 8004
ENTRYPOINT ["./instrument.sh", "dotnet", "billing-service.dll"]
```

> **Note sur le warning au démarrage** : tu verras le message `Unable to locate the native profiler inside current directory`. C'est attendu et sans impact : ASP.NET Core et HttpClient utilisent l'Activity API de .NET (pas le profiler CLR natif), donc les spans sont générés correctement via le startup hook. Le profiler natif est utile uniquement pour instrumenter des bibliothèques tierces qui ne publient pas d'ActivitySource.

> **Pourquoi `instrument.sh` est nécessaire** : lancer `dotnet billing-service.dll` directement n'activerait pas le profiler CLR. `instrument.sh` positionne les variables d'environnement requises avant de déléguer à `dotnet` :
>
> ```text
> CORECLR_ENABLE_PROFILING=1
> CORECLR_PROFILER={918728DD-259F-4A6A-AC2B-B85E1B658318}
> CORECLR_PROFILER_PATH=./OpenTelemetry.AutoInstrumentation.Native.so
> DOTNET_ADDITIONAL_DEPS=./AdditionalDeps
> DOTNET_SHARED_STORE=./store
> DOTNET_STARTUP_HOOKS=./net/OpenTelemetry.AutoInstrumentation.StartupHook.dll
> ```
>
> Sans ces variables, le runtime .NET démarre normalement, sans aucune instrumentation.

### 3. Configurer via variables d'environnement

Dans [docker-compose.yml](../../../docker-compose.yml), bloc `billing-service`, ajouter les variables d'environnement :

```yaml
  billing-service:
    build:
      context: .
      dockerfile: src/billing-service/Dockerfile
    volumes:
      - billing-data:/data
    environment:
      - FLAGD_HOST=flagd
      - FLAGD_PORT=8013
      - OTEL_SERVICE_NAME=billing-service           # nom du service dans les spans
      - OTEL_TRACES_EXPORTER=console                # export des traces sur stdout
      - OTEL_METRICS_EXPORTER=console               # export des métriques sur stdout
      - OTEL_LOGS_EXPORTER=console                  # export des logs sur stdout
    depends_on:
      - users-service
      - products-service
      - payment-service
      - flagd
```

> **Toute la configuration passe par des variables d'environnement** — c'est le contrat de l'auto-instrumentation zero-code. Pas de `appsettings.json`, pas de code à modifier.
>
> | Variable | Rôle |
> |---|---|
> | `OTEL_SERVICE_NAME` | Nom du service dans les spans (`service.name`) |
> | `OTEL_TRACES_EXPORTER=console` | Export des traces sur stdout |
> | `OTEL_METRICS_EXPORTER=console` | Export des métriques sur stdout |
> | `OTEL_LOGS_EXPORTER=console` | Export des logs sur stdout |

### 4. Builder et démarrer

```bash
task up
```

### 5. Générer du trafic

Récupère l'identifiant d'un produit, puis passe une commande :

```bash
# Récupère le premier product_id disponible
PRODUCT_ID=$(curl -s http://localhost:8080/api/products | \
  python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")

curl -X POST http://localhost:8080/api/billing/checkout \
  -H "Content-Type: application/json" \
  -d "{
    \"user_id\": \"2\",
    \"items\": [{\"product_id\": \"$PRODUCT_ID\", \"quantity\": 1}]
  }"
```

> **Pourquoi des UUIDs ?** Les IDs produits et utilisateurs sont des UUIDs générés à la création — pas des entiers séquentiels. Les champs `user_id` et `product_id` doivent également être passés comme **strings** (entre guillemets) car les modèles C# les déclarent en `string`.

Réponse attendue :

- `200 OK` avec un JSON `{ "order_id": ..., "total": ..., "status": "confirmed" }` si le stock est suffisant et le paiement approuvé.
- `402 Payment Required` si la banque refuse (environ 10 % des cas — comportement métier attendu, pas un bug).
- `409 Conflict` si le stock est insuffisant.

### 6. Observer les spans

```bash
task logs -- billing-service | grep -A 20 "Activity\."
```

Ou pour voir l'ensemble des sorties OTel :

```bash
task logs -- billing-service 2>&1 | grep -E "(Activity|SpanId|TraceId|service\.name|http\.)"
```

## Analyse : ce qu'on obtient, ce qu'on ne contrôle pas

| Capacité | Auto-instrumentation |
|---|---|
| Span serveur HTTP (`POST /checkout`) | ✅ automatique |
| Spans HttpClient sortants (vers `products-service`, `payment-service`) | ✅ automatique |
| Propagation W3C Trace Context sur les appels sortants | ✅ automatique |
| `service.name` dans les spans | ✅ via `OTEL_SERVICE_NAME` |
| Attributs métier (`order.id`, `user.id`, montant…) | ❌ impossible sans code |
| Spans custom (ex. : span dédié au calcul de taxe) | ❌ impossible sans code |
| Métriques custom (ex. : compteur de commandes, montant moyen) | ❌ impossible sans code |
| Configuration fine des exporters (OTLP, batch, retry…) | ❌ limité aux variables d'environnement standards |

**Conclusion** : l'auto-instrumentation est un excellent point de départ — zéro code, observabilité réseau immédiate, propagation distribuée fonctionnelle. Mais elle ne peut pas extraire de contexte métier : le profiler CLR instrumente les bibliothèques, pas la logique applicative. Pour ajouter `order.id` sur un span, créer un span dédié au calcul de taxe, ou émettre une métrique custom, il faut l'approche **programmatique** des steps suivants.

## Critères de validation

- [ ] `billing-service` démarre sans erreur (`task logs -- billing-service` ne montre pas d'exception au démarrage).
- [ ] Un `POST /api/billing/checkout` produit des spans dans les logs du conteneur.
- [ ] Les spans contiennent `service.name = billing-service`.
- [ ] Au moins un span serveur HTTP (`POST /checkout`, `SpanKind = SERVER`) est visible.
- [ ] Des spans HttpClient (`SpanKind = CLIENT`) correspondant aux appels vers `products-service` et `payment-service` sont visibles.

## Reset : retour à `main` propre

> **Attention** : cette commande supprime **toutes les modifications non committées** dans le dépôt.

```bash
git reset --hard
git clean -fd
```

Vérifier l'état du dépôt :

```bash
git status
```

Résultat attendu :

```text
On branch main
nothing to commit, working tree clean
```

`billing-service.csproj` et `Dockerfile` retrouvent leur état d'origine (sans le package `OpenTelemetry.AutoInstrumentation` et avec l'`ENTRYPOINT` original `dotnet billing-service.dll`). Les variables d'environnement dans `docker-compose.yml` sont également supprimées.

## Étape suivante

Une fois l'étape validée (ou si tu préfères passer directement à l'approche programmatique), direction [step1-add-traces.md](step1-add-traces.md).

Sur la branche `main`, l'exercice step 1 consiste à configurer le **SDK OpenTelemetry .NET** programmatiquement — `TracerProvider`, instrumentations ASP.NET Core et HttpClient, export console — et à observer la même chaîne de traces, mais cette fois avec la possibilité d'ajouter des attributs métier et des spans custom.
