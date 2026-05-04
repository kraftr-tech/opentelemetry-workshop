# Workshop .NET — Vue d'ensemble

Bienvenue dans le **track .NET** de l'atelier OpenTelemetry.

## Objectifs pédagogiques

À l'issue de l'atelier, tu seras capable de :

- Démontrer l'**auto-instrumentation zero-code** (`OpenTelemetry.AutoInstrumentation`) et comprendre ses limites.
- Configurer le **SDK OpenTelemetry .NET** programmatiquement (`TracerProvider`, `MeterProvider`, logging bridge) dans un service ASP.NET Core.
- Instrumenter automatiquement les spans ASP.NET Core (HTTP serveur) et HttpClient (HTTP client) avec les packages d'instrumentation.
- Déployer un **collecteur OTel** et configurer les pipelines receivers/processors/exporters.
- Ajouter des **spans custom** (`ActivitySource`) et des **métriques custom** (`Meter`) pour enrichir la télémétrie métier.
- Visualiser et corréler les 3 signaux dans Grafana.

## Service instrumenté : `billing-service`

`billing-service` est l'**orchestrateur du checkout** : il valide le stock, calcule le total, déclenche le paiement et enregistre la commande. Il appelle trois services via HttpClient :

```text
UI → billing-service → products-service  (validation stock, mise à jour)
                     → payment-service   (traitement paiement)
                     → bank-service      (via payment, gRPC — déjà instrumenté)
```

C'est ce qui rend ce service intéressant pour l'instrumentation : **un serveur HTTP + trois clients HTTP sortants**, avec propagation W3C Trace Context sur chaque appel.

## Format

- Durée : 3 h.
- Prérequis : connaissances C#/.NET et Docker de base.
- Support : slides + ce repo.

## Branches git

| Branche | État de départ | Exercice sur cette branche |
|---|---|---|
| `main` | Application non instrumentée | **Step 0** : auto-instrumentation démo, puis **Step 1** : traces programmatiques |
| `step-2-metrics` | Tous les services Python instrumentés pour les traces | **Step 2** : métriques `billing-service` |
| `step-3-logs` | + métriques partout | **Step 3** : logs `billing-service` |
| `step-4-collector` | + logs partout (export OTLP activé) | **Step 4** : collecteur OTel |
| `step-5-custom-metrics` | + collecteur en place | **Step 5** : instrumentation custom `billing-service` |
| `final-state` | Stack instrumentée de bout en bout | — |

> Sur `step-2-metrics`, tous les services Python sont déjà instrumentés pour les traces (travail de l'animateur). La chaîne complète `billing→payment→bank` devient visible dès ce point.

## Navigation entre les étapes

```bash
# Étape courante
git branch --show-current

# Passer à l'étape suivante
git checkout step-2-metrics
```

## Avant de commencer

1. Lire [../00-setup.md](../00-setup.md) — installation des outils et vérifications.
2. Ouvrir Grafana à <http://localhost:8080/grafana> pour valider la stack (accessible après `task up`).
3. Ouvrir [step0-auto-instrumentation.md](step0-auto-instrumentation.md).
