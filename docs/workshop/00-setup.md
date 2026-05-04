# Setup du workshop

À faire **une seule fois** avant le début de l'atelier. Compte ~15 min avec une bonne connexion.

## 1. Outils

```bash
# Cloner le repo
git clone https://github.com/kraftr-tech/opentelemetry-workshop.git
cd opentelemetry-workshop

# Installer les outils via mise (Go, Node, protoc…)
mise install
```

Si tu n'as pas `mise` : <https://mise.jdx.dev/getting-started.html>.

Alternative sans mise : installer manuellement `docker`, `python`, `go`... — versions dans [mise.toml](../../mise.toml).

## 2. Docker démarré

```bash
docker info
```

Doit répondre sans erreur.

## 3. Première exécution Docker Compose

Sanity check local :

```bash
task up
```

Ouvrir <http://localhost:8080>. Se connecter avec `john.doe@kraftr.tech` / `client123`. Ajouter un produit au panier, faire un checkout. Si tu vois un écran "Order confirmed" ou un déclin (`402`), **tout roule**.

Arrêter :

```bash
task down
```

## 4. Accéder à la démo

```bash
task open:site
```

## Commandes disponibles

Toutes les commandes utiles sont exposées via [Task](https://taskfile.dev). `task` seul (ou `task --list`) affiche la liste à jour.

**Stack** :

| Commande | Action |
|---|---|
| `task up` | Démarrer la stack (build + run en arrière-plan) |
| `task down` | Arrêter la stack et supprimer les volumes |
| `task logs` | Suivre les logs de l'`otel-collector` |
| `task logs -- <service>` | Suivre les logs d'un service précis (ex. `task logs -- billing-service`) |

**Raccourcis navigateur** :

| Commande | Ouvre |
|---|---|
| `task open:site` | <http://localhost:8080> — boutique Atelier |
| `task open:grafana` | <http://localhost:8080/grafana> — dashboards, Explore (traces/metrics/logs) |
| `task open:loadgen` | <http://localhost:8080/loadgen> — Locust (générateur de charge) |
| `task open:feature` | <http://localhost:8080/feature> — UI flagd (feature flags) |

**Tâches avancées** (rarement nécessaires pendant l'atelier) :

| Commande | Action |
|---|---|
| `task build` | Reconstruit toutes les images Docker locales |
| `task build:<service>` | Rebuild une image précise (ex. `task build:ui`) |
| `task proto:bank` | Régénère les stubs gRPC Go pour `bank-service` après modification du `.proto` |


## Tu es prêt

Direction [README.md](README.md) pour choisir ton track et commencer le premier exercice.

En cas de pépin, voir [docs/05-troubleshooting.md](../05-troubleshooting.md).
