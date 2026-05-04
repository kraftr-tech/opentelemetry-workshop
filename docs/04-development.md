# Développement

Guide pour contribuer au projet ou développer un service isolément.

## Outils

Toutes les versions d'outils sont pilotées par [mise.toml](../mise.toml) :

- `go` 1.25 — bank-service
- `node` 24 — UI
- `protoc` 25.9 + plugins Go — stubs gRPC
- `task` — runner (cf. [Taskfile.yaml](../Taskfile.yaml))
- `k6` — tests de charge

```bash
mise install
```

## Tâches disponibles

```bash
task --list
```

### Stack locale (Docker Compose)

| Tâche              | Description                                                |
| ------------------ | ---------------------------------------------------------- |
| `task up`    | `docker compose up -d --build` avec env mise injecté       |
| `task down`  | `docker compose down -v` (supprime les volumes)            |
| `task logs`  | `docker compose logs -f otel-collector` (ou autre via CLI) |

### Build des images

| Tâche                          | Description                                            |
| ------------------------------ | ------------------------------------------------------ |
| `task build`                   | Build toutes les images Docker                         |
| `task build:<service>`         | Build une seule image (`ui`, `users-service`, …)       |

### Divers

| Tâche                         | Description                                   |
| ----------------------------- | --------------------------------------------- |
| `task proto:bank`             | Régénère les stubs Go de bank-service         |

## Structure du code

Cf. [02-architecture.md](02-architecture.md#structure-du-code-par-service) pour le layout en couches.

## Développer un service Python seul

```bash
cd users-service
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
python app.py          # Flask dev server, pas gunicorn
```

Le service écoute sur son port natif (8001 pour `users`, etc.). Les autres services appelés doivent tourner en parallèle (via `task up -- <service>`) ou être mockés.

## Développer le billing-service (.NET) seul

```bash
cd billing-service
dotnet run             # ASP.NET Core sur http://localhost:8004
```

Pour les tests unitaires :

```bash
cd billing-service.tests
dotnet test
```

Le service utilise SQLite avec le chemin par défaut `/data/billing.db` (configurable via `DATABASE_PATH`). Les services `products-service` et `payment-service` sont appelés via `PRODUCTS_SERVICE_URL` et `PAYMENT_SERVICE_URL`.

## Développer l'UI seule

Voir [ui/README.md](../ui/README.md) — `npm install && npm run dev`, proxy Vite configurable via `.env.local`.

## Régénérer les stubs gRPC

Modifications du contrat dans [proto/transaction.proto](../proto/transaction.proto) :

```bash
task proto:bank
```

Les stubs Python de `payment-service` sont régénérés automatiquement au build Docker (voir [payment-service/Dockerfile](../payment-service/Dockerfile)).

## Tests de charge

Voir [src/load-generator/README.md](../src/load-generator/README.md).

## Conventions

- Les services Python (`users`, `products`, `payment`) tournent **toujours** avec gunicorn en prod (jamais le dev server Flask).
- Le `billing-service` (.NET) tourne avec le runtime ASP.NET Core (`dotnet billing-service.dll`).
- Tous les services utilisent SQLite avec volume persistant (`/data/*.db`).
