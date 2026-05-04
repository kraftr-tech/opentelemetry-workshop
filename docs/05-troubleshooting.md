# Troubleshooting

Erreurs les plus fréquemment rencontrées pendant l'atelier.

## Docker Compose

### `port is already allocated`

Un autre process écoute déjà sur le port `8080`.

```bash
lsof -iTCP:8080 -sTCP:LISTEN   # identifier le coupable
```

Arrête-le, ou modifie le mapping dans [docker-compose.yml](../docker-compose.yml).

> Seul le proxy Envoy (`frontend-proxy`) expose un port sur l'hôte : `8080`. Tous les services — y compris l'UI, le load generator (`/loadgen/`), Grafana (`/grafana/`) et les APIs — sont accessibles via ce port unique. Les ports internes des services (`8001`–`8004`, `4317`…) ne sont jamais exposés sur l'hôte.

### L'app n'affiche pas les produits

Vérifier que `products-service` est bien up :

```bash
docker compose ps
curl http://localhost:8080/products/health
```

Si la base est vide, le seed ne s'est pas joué — reset :

```bash
task down && task up
```

### Le collecteur boucle en erreur d'auth

Les backends Grafana ne reçoivent pas encore les données. Vérifier que les exporters sont bien branchés dans les pipelines du collecteur — voir [03-observability.md](03-observability.md).

## gRPC / bank-service

### `connection refused` depuis payment-service

`BANK_SERVICE_HOST` ne pointe pas au bon endroit.

- En Compose : doit être `bank-service:50051`.

### Taux de déclin anormalement élevé

Normal — le `bank-service` simule ~10 % de déclin aléatoire. Les réponses HTTP 402 renvoyées par `billing-service` ne sont **pas** des erreurs applicatives. Voir [k6/README.md](../k6/README.md).

## Où creuser ensuite

- Logs collecteur : `task logs`
- Logs d'un service Compose : `task logs -- <service>`
