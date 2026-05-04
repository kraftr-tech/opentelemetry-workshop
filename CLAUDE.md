# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OpenTelemetry hands-on workshop built around "Atelier", an e-commerce application composed of 6 microservices. The goal is to instrument these services with OpenTelemetry.

## Architecture

```
Browser / Load Generator
        │
        ▼
frontend-proxy (Envoy :8080)   ← point d'entrée unique
├── /api/products  → products-service (:8002)  [Flask/SQLite]
├── /api/users     → users-service (:8001)     [Flask/SQLite]
├── /api/billing/  → billing-service (:8004)   [.NET 10/ASP.NET Core]
│                      ├── products-service     [HTTP - validation & mise à jour stock]
│                      └── payment-service (:8003) [Flask/SQLite]
│                             └── bank-service (:50051) [Go/gRPC]
├── /api/payments  → payment-service (:8003)
├── /loadgen/      → load-generator (:8089)
├── /otlp-http/    → otel-collector (HTTP :4318)
└── /              → ui (:3000)               [React/Vite]
```

**Repository layout:**
```
src/
  bank-service/          (Go/gRPC)
  billing-service/       (.NET 10)
  billing-service.tests/ (.NET 10 tests)
  frontend-proxy/        (Envoy proxy - envoy.tmpl.yaml + Dockerfile)
  load-generator/        (Locust)
  otel-collector/        (otelcol-config.yaml)
  payment-service/       (Flask/Python)
  products-service/      (Flask/Python)
  users-service/         (Flask/Python)
  ui/                    (React/Vite)
proto/                   (contrat gRPC partagé)
docs/
```

**Communication patterns:**
- Client externe → services : HTTP via Envoy proxy (port 8080), qui route et réécrit les préfixes
- Envoy → OTel Collector : traces gRPC (port 4317), logs d'accès OTel
- billing-service → products-service, payment-service, users-service: HTTP REST
- payment-service → bank-service: gRPC (proto contract at `proto/transaction.proto`)

**Envoy frontend-proxy (`src/frontend-proxy/`):**
- Config template `envoy.tmpl.yaml` → variables substituées via `envsubst` au démarrage du conteneur
- Image : `envoyproxy/envoy:v1.34-latest`
- Tracing OTel natif (`envoy.tracers.opentelemetry`) vers l'OTel Collector en gRPC
- Access logging OTel (`envoy.access_loggers.open_telemetry`) vers l'OTel Collector
- Filtre de fault injection HTTP configurable via headers
- Interface d'administration Envoy sur le port 9901
- Réécriture de préfixes : `/api/products` → `/products`, `/api/users` → `/users`, `/api/billing/` → `/`, `/api/payments` → `/payments`

**Service responsibilities:**
- **users-service**: authentication, user management, shopping cart
- **products-service**: product catalog, stock management
- **billing-service**: checkout orchestration (validates stock, computes totals with tax/shipping, processes payment, creates orders), order storage
- **payment-service**: payment processing, calls bank-service via gRPC
- **bank-service**: simulates bank transaction processing (100-500ms delay, ~10% decline rate)

**Python service structure** (users, products, payment):
```
app.py      → Handlers (Flask routes, input validation, HTTP responses)
service.py  → Business logic (orchestration, no Flask imports)
db.py       → Database layer (SQLite connection, queries, schema init)
```

**C#/.NET service structure** (billing):
```
Program.cs           → Routes Minimal API, DI, init SQLite
BillingService.cs    → Business logic (checkout, summary, orders)
BillingRepository.cs → SQLite layer (INSERT / SELECT)
Models.cs            → Shared DTOs
```

**Go service structure** (bank):
```
main.go     → Entry point (gRPC server startup)
handler.go  → gRPC handler (request/response mapping)
service.go  → Business logic (transaction processing)
```

## Commands

### Prerequisites
```bash
mise install          # Installs go, node, protoc, protoc-gen-go, protoc-gen-go-grpc
```

### Run the full stack
```bash
task up
```
App available at http://localhost:8080

### Generate protobuf stubs for bank-service
```bash
task proto:bank
```
Python stubs for payment-service are generated at Docker build time in its Dockerfile.

### UI development
```bash
cd src/ui && npm install
cd src/ui && npm run dev      # Dev server
cd src/ui && npm run build    # Production build
cd src/ui && npm run lint     # TypeScript check (tsc --noEmit)
```

## Key Details

- Python services (`users`, `products`, `payment`) run with **gunicorn** (not Flask dev server)
- `billing-service` runs with **ASP.NET Core** (`dotnet billing-service.dll`) on port 8004
- All services use **SQLite** with persistent Docker volumes
- **Envoy** est le proxy frontal unique (port 8080) ; le dev server Vite n'est pas exposé directement en production Docker
- The UI is a single-file React app (`src/ui/src/App.tsx`) with all screens and components
- Two user roles: **admin** (backoffice only) and **client** (shop, cart, checkout)
- Seed data is auto-created on first run (2 users: `admin@kraftr.tech`/`admin123`, `john.doe@kraftr.tech`/`client123`)
- The billing `/api/billing` proxy rewrites to `/` (unlike other proxies that rewrite `/api` to `/`)
