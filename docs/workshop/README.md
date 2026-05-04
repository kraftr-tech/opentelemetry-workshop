# Workshop — OpenTelemetry, instrumentation d'une stack microservices

Bienvenue dans l'atelier **OpenTelemetry — Instrumentation d'une stack microservices**.

Ce workshop propose **deux tracks** selon le langage que tu veux pratiquer. Choisis ton track ci-dessous et suis le guide dédié jusqu'à la fin — les deux tracks couvrent les mêmes concepts OpenTelemetry, sur des services différents.

## Choisir son track

| | Track Python | Track .NET |
|---|---|---|
| **Service instrumenté** | `payment-service` | `billing-service` |
| **Framework** | Flask | ASP.NET Core 10 |
| **Communication sortante** | gRPC (vers `bank-service`) | HTTP (vers `products`, `payment`, `users`) |
| **Étapes** | 5 | 6 (step 0 inclus) |
| **Prérequis** | Python, Docker | C#/.NET, Docker |

### → [Track Python](python/README.md)

`payment-service` Flask + client gRPC. Montre la propagation de contexte Python → Go dès l'étape 1.

### → [Track .NET](dotnet/README.md)

`billing-service` ASP.NET Core + HttpClient. Commence par une démo d'auto-instrumentation zero-code (step 0) avant l'approche programmatique.

---

## Setup commun

Avant de commencer l'un ou l'autre des tracks : [00-setup.md](00-setup.md)

## Ressources

- [docs/02-architecture.md](../../02-architecture.md) — schéma et ports
- [docs/05-troubleshooting.md](../../05-troubleshooting.md) — erreurs fréquentes
