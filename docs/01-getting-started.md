# Démarrage rapide

Démarre la stack **Atelier** en local avec Docker Compose.

## Prérequis

- [Docker](https://docs.docker.com/get-docker/) avec le plugin `compose`
- [mise](https://mise.jdx.dev) — gère les versions des outils (Go, Node, protoc, k6…)

Les versions exactes sont déclarées dans [mise.toml](../mise.toml).

```bash
mise install
```

## Lancer la stack

```bash
task up
```

Six conteneurs démarrent (5 microservices + UI), plus un collecteur OpenTelemetry et la stack Grafana (Grafana, Loki, Tempo, VictoriaMetrics).

L'application est disponible sur **<http://localhost:8080>**.

## Se connecter

Deux comptes sont créés au premier démarrage :

| Rôle           | Email                     | Mot de passe |
| -------------- | ------------------------- | ------------ |
| Administrateur | `admin@kraftr.tech`       | `admin123`   |
| Client         | `john.doe@kraftr.tech` | `client123`  |

- **Admin** → accès au backoffice (gestion produits).
- **Client** → shop, panier, checkout.

## Vérifier que tout tourne

Tailer les logs du collecteur (ou d'un autre service via `CLI_ARGS`) :

```bash
task logs                          # otel-collector par défaut
task logs -- users-service         # autre service
```

Santé des services :

```bash
curl http://localhost:8080/users/health     # users-service
curl http://localhost:8080/products/health  # products-service
curl http://localhost:8080/payments/health  # payment-service
curl http://localhost:8080/billing/health   # billing-service
```

Simuler du trafic avec Load Generator :

```bash
curl http://localhost:8080/loadgen/start?users=10&duration=10m
```

## Arrêter la stack

```bash
task down
```

> `task down` passe l'option `-v` : les volumes SQLite sont **supprimés**, chaque redémarrage repart donc sur un seed frais. Si tu veux conserver les données, reste avec `docker compose down` (sans `-v`) à la main.

## Étape suivante

- Pour le workshop : [workshop/README.md](workshop/README.md)
