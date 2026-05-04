# Étape 5 — Instrumentation custom : spans métier et métriques applicatives

> Branche : `step-5-custom-metrics`
> Durée estimée : 35 min
> État à l'arrivée : les trois signaux (traces, métriques, logs) sortent via le collecteur vers Grafana local (Tempo, VictoriaMetrics, Loki). La télémétrie actuelle est purement **technique** : elle décrit le protocole (HTTP, gRPC), la structure (serveurs, clients), mais pas le **métier**. On va enrichir ça.

## Préparation : passer sur la branche `step-5-custom-metrics`

Tu arrives de l'exo 4 avec les trois signaux qui transitent vers Grafana. On les **écarte** pour récupérer l'état propre de `step-5-custom-metrics` (solution de référence de l'exo 4 + point de départ de l'exo 5).

```bash
# 1. Annule toutes les modifications locales sur les fichiers suivis
git reset --hard

# 2. Supprime les fichiers non suivis
git clean -fd

# 3. Bascule sur la branche de l'exo 5
git checkout step-5-custom-metrics
```

> ⚠️ Ces commandes **détruisent** ton travail local de l'exo 4. Si tu veux conserver ta solution, fais d'abord un `git stash` ou un commit sur une branche perso avant la réinitialisation.

## Ce que tu vas apprendre

- Créer des **spans custom** (manuels) pour capturer des concepts métier : étapes du flux de paiement, décisions (approbation, déclin), latences métier.
- Enrichir les spans avec des **attributs métier** : identifiants, montants, états de validation.
- Créer des **événements** (`span.add_event`) pour marquer les transitions critiques dans un span.
- Ajouter des **métriques custom** : compteurs applicatifs (`payments_total`, `payments_declined`), jauges (solde), histogrammes (durée de traitement métier).
- Comprendre la différence entre **erreurs techniques** (exceptions, status HTTP 5xx) et **erreurs métier** (refus de paiement bancaire) — et comment les représenter différemment dans OTel.
- Visualiser et corréler ces signaux métier dans Grafana pour **alerter intelligemment**.

---

## Partie A — Spans custom : anatomie du flux de paiement

L'auto-instrumentation Flask donne déjà un span pour `POST /payments`. Mais elle n'en sait rien des étapes internes : vérification des feature flags, appel à la banque, enregistrement en base. On va cartographier ça explicitement.

### A.1. Créer un span parent pour le flux métier

Modifier [payment-service/service.py](../../../src/payment-service/service.py) — importer les APIs et créer un span parent qui englobe tout le traitement :

```python
from opentelemetry import trace

logger = logging.getLogger("payments.service")
tracer = trace.get_tracer(__name__)

def process_payment(user_id, amount):
    with tracer.start_as_current_span("process.payment") as span:
        span.set_attribute("user_id", user_id)
        span.set_attribute("amount", amount)

        # Étape 1 : validation (feature flags + règles métier)
        with tracer.start_as_current_span("validate.payment") as val_span:
            flags = client()

            if flags.get_boolean_value("paymentUnreachable", False):
                val_span.set_attribute("validation.status", "failed")
                val_span.add_event("validation_failed", {"reason": "paymentUnreachable"})
                raise PaymentUnreachableError("payment service is configured as unreachable")

            fail_rate = flags.get_float_value("paymentFailure", 0.0)
            if fail_rate > 0 and random.random() < fail_rate:
                val_span.set_attribute("validation.status", "failed")
                val_span.add_event("validation_failed", {"reason": "simulated_failure", "fail_rate": fail_rate})
                raise PaymentSimulatedFailureError(
                    f"simulated charge failure (rate={fail_rate})"
                )

            delay_ms = flags.get_integer_value("paymentSlow", 0)
            if delay_ms > 0:
                logger.info("payment_slow_delay_applied", extra={"delay_ms": delay_ms, "user_id": user_id})
                time.sleep(delay_ms / 1000)

            val_span.set_attribute("validation.status", "passed")

        # insert_payment crée l'enregistrement en état "pending" avant l'appel bancaire
        payment_id = insert_payment(user_id, amount)
        logger.info("payment_pending", extra={"payment_id": payment_id, "user_id": user_id, "amount": amount})

        # Étape 2 : appel bancaire via gRPC
        with tracer.start_as_current_span("call.bank") as bank_span:
            try:
                channel = grpc.insecure_channel(BANK_SERVICE_HOST)
                stub = transaction_pb2_grpc.TransactionServiceStub(channel)
                grpc_request = transaction_pb2.TransactionRequest(
                    merchant_id="atelier-store",
                    amount=amount,
                )
                grpc_response = stub.ProcessTransaction(grpc_request, timeout=10)

                approved = grpc_response.status == "approved"
                bank_span.set_attribute("bank_response.approved", approved)
                if approved:
                    bank_span.add_event("bank_approved", {"transaction_id": grpc_response.transaction_id})
                else:
                    bank_span.add_event("bank_declined", {
                        "transaction_id": grpc_response.transaction_id,
                        "status": grpc_response.status,
                    })

            except grpc.RpcError as e:
                bank_span.record_exception(e)
                update_payment_status(payment_id, "failed")
                logger.error("bank_transaction_rpc_error", extra={
                    "payment_id": payment_id,
                    "grpc_code": e.code().name if e.code() else "UNKNOWN",
                    "details": e.details(),
                })
                return None, f"bank transaction failed: {e.details()}"

        # Étape 3 : enregistrement du résultat en base
        with tracer.start_as_current_span("record.payment") as record_span:
            update_payment_status(
                payment_id,
                grpc_response.status,
                grpc_response.transaction_id,
                grpc_response.decline_reason or None,
            )
            payment = find_payment_by_id(payment_id)
            if grpc_response.status == "declined":
                reason = grpc_response.decline_reason or "unknown"
                logger.warning("bank_transaction_completed", extra={
                    "payment_id": payment_id,
                    "transaction_id": grpc_response.transaction_id,
                    "status": grpc_response.status,
                    "reason": reason,
                })
            else:
                logger.info("bank_transaction_completed", extra={
                    "payment_id": payment_id,
                    "transaction_id": grpc_response.transaction_id,
                    "status": grpc_response.status,
                })
            record_span.set_attribute("payment_id", payment_id)
            record_span.set_attribute("transaction_id", grpc_response.transaction_id)
            record_span.set_attribute("recorded", True)

        span.set_attribute("status", grpc_response.status)

        if grpc_response.status == "declined":
            return row_to_dict(payment), grpc_response.decline_reason or "unknown"

        return row_to_dict(payment), None
```

Points clés :

- **`tracer.start_as_current_span("process.payment")`** : crée un span nommé `process.payment` qui sera **enfant** du span HTTP du serveur (l'instrumentation Flask l'aura propagé dans le contexte actif).
- **`validate.payment`** : regroupe toutes les décisions amont — feature flags (`paymentUnreachable`, `paymentFailure`, `paymentSlow`) — dans un seul span. Si un flag déclenche un échec, on pose l'événement `validation_failed` avant de lever l'exception.
- **`call.bank`** : englobe l'appel gRPC réel. L'auto-instrumentation gRPC crée déjà un span client à l'intérieur ; `call.bank` l'enveloppe pour ajouter le contexte métier (`bank_response.approved`, événements `bank_approved` / `bank_declined`).
- **`record.payment`** : contient `update_payment_status` + `find_payment_by_id`. `grpc_response` reste accessible en Python (portée de fonction, pas de bloc) même après la sortie du `with call.bank`.
- **`span.record_exception(e)`** : enregistre l'exception **sans** la relancer. Utile pour capturer des erreurs qu'on gère (ici le `RpcError`).
- **Spans enfants** : chaque `with tracer.start_as_current_span(...)` imbriqué crée un **span enfant** du contexte courant. Tempo les affichera en hiérarchie.

### A.2. Ajouter une métrique de compteur custom

Toujours dans [payment-service/service.py](../../../src/payment-service/service.py), ajouter les imports et la configuration du compteur :

```python
from opentelemetry import metrics

meter = metrics.get_meter(__name__)
payments_counter = meter.create_counter(
    name="payments_total",
    description="Total number of payment requests processed",
    unit="1"  # dimensionless
)
payments_declined_counter = meter.create_counter(
    name="payments_declined_total",
    description="Total number of declined payments",
    unit="1"
)
```

Puis, dans `process_payment()`, incrémenter les compteurs aux points clés :

```python
def process_payment(user_id, amount):
    with tracer.start_as_current_span("process.payment") as span:
        span.set_attribute("user_id", user_id)
        span.set_attribute("amount", amount)
        payments_counter.add(1, {"status": "attempted"})

        with tracer.start_as_current_span("validate.payment") as val_span:
            flags = client()

            if flags.get_boolean_value("paymentUnreachable", False):
                val_span.set_attribute("validation.status", "failed")
                val_span.add_event("validation_failed", {"reason": "paymentUnreachable"})
                payments_declined_counter.add(1, {"reason": "unreachable"})
                raise PaymentUnreachableError(...)

            fail_rate = flags.get_float_value("paymentFailure", 0.0)
            if fail_rate > 0 and random.random() < fail_rate:
                val_span.set_attribute("validation.status", "failed")
                val_span.add_event("validation_failed", {"reason": "simulated_failure", "fail_rate": fail_rate})
                payments_declined_counter.add(1, {"reason": "simulated"})
                raise PaymentSimulatedFailureError(...)

            # ... paymentSlow, insert_payment ...
            val_span.set_attribute("validation.status", "passed")

        with tracer.start_as_current_span("call.bank") as bank_span:
            try:
                # ... appel gRPC ...
                if not approved:
                    payments_declined_counter.add(1, {"reason": "bank_declined"})
            except grpc.RpcError as e:
                bank_span.record_exception(e)
                # ... update_payment_status + return ...

        with tracer.start_as_current_span("record.payment") as record_span:
            # ... update_payment_status + find_payment_by_id ...

        payments_counter.add(1, {"status": "approved"})
        span.set_attribute("status", grpc_response.status)
        return row_to_dict(payment), None
```

> Les compteurs peuvent avoir un `description` et un `unit`. Le `unit` est une convention (voir [UCUM](https://ucum.org/) pour les standards) — ici on laisse `"1"` pour sans-dimension. Les **attributs** (dico passé en second argument) deviennent des labels Prometheus : tu pourras faire un `rate(payments_total{status="approved"}[1m])` en Grafana.

### A.3. Importer et utiliser dans `app.py`

Modifier [payment-service/app.py](../../../src/payment-service/app.py) — s'assurer que `service.process_payment()` est appelée où elle doit l'être :

```python
from service import process_payment

@app.route("/payments", methods=["POST"])
def create_payment():
    data = request.get_json()
    if not data or not data.get("user_id") or data.get("amount") is None:
        return jsonify({"error": "user_id and amount are required"}), 400

    payment, error = process_payment(data["user_id"], data["amount"])
    if error and payment:
        # Refus bancaire → 402, corps contient les données du paiement + la raison
        return jsonify({**payment, "error": error}), 402
    if error:
        # Erreur technique gRPC → 502
        return jsonify({"error": error, "payment_id": None}), 502

    return jsonify(payment), 201
```

(Pas de changement majeur — c'est juste pour vérifier que le flow n'est pas cassé.)

### A.4. Builder et tester

```bash
task build:payment-service
task up
```

Générer du trafic avec succès et erreurs :

```bash
# Paiement valide
curl -X POST http://localhost:8003/payments \
  -H "Content-Type: application/json" \
  -d '{"user_id": "john", "amount": 50.00}'
```

### A.5. Observer les spans et événements

Dans Grafana / Tempo, cherche une trace avec `service.name = payment-service`. Elle doit maintenant contenir :

- 1 span root : `POST /payments` (Flask auto-instrument)
  - 1 span enfant : `process.payment` (custom)
    - 1 span enfant : `validate.payment`
    - 1 span enfant : `call.bank` (qui lui-même contient un span gRPC enfant via l'instrumentation)
    - 1 span enfant : `record.payment`

Déplie chaque span pour voir ses attributs et événements. Par exemple, `call.bank` doit montrer un événement `bank_approved` ou `bank_declined`.

### A.6. Métriques dans Grafana

Dans Prometheus ou Explorer > Metrics, cherche :

```
payments_total{service_name="payment-service", status="attempted"}
payments_total{service_name="payment-service", status="approved"}
payments_declined_total{service_name="payment-service", reason="bank_declined"}
payments_declined_total{service_name="payment-service", reason="unreachable"}
payments_declined_total{service_name="payment-service", reason="simulated"}
```

Tu dois voir les compteurs augmenter avec tes requêtes.

---

## Partie B — Histogramme custom pour la latence métier

Les compteurs sont cumulatifs. Pour mesurer des **durées**, on utilise un histogramme — une distribution qui aggrège des valeurs dans des buckets.

### B.1. Créer un histogramme

Modifier [payment-service/service.py](../../../src/payment-service/service.py) — ajouter aux définitions de métriques :

```python
payment_latency_histogram = meter.create_histogram(
    name="payment_processing_duration_ms",
    description="Time spent processing a payment request (business logic only)",
    unit="ms"
)
```

### B.2. Enregistrer les mesures au début et à la fin

Modifier `process_payment()` — capturer le timestamp avant le span et enregistrer l'histogramme après :

```python
def process_payment(user_id, amount):
    start_time = time.time()
    outcome = "unknown"

    with tracer.start_as_current_span("process.payment") as span:
        span.set_attribute("user_id", user_id)
        span.set_attribute("amount", amount)
        payments_counter.add(1, {"status": "attempted"})

        with tracer.start_as_current_span("validate.payment") as val_span:
            # ... vérification des flags ...
            if flags.get_boolean_value("paymentUnreachable", False):
                # ...
                outcome = "declined"
                payments_declined_counter.add(1, {"reason": "unreachable"})
                raise PaymentUnreachableError(...)

            fail_rate = flags.get_float_value("paymentFailure", 0.0)
            if fail_rate > 0 and random.random() < fail_rate:
                # ...
                outcome = "declined"
                payments_declined_counter.add(1, {"reason": "simulated"})
                raise PaymentSimulatedFailureError(...)

            val_span.set_attribute("validation.status", "passed")

        # ... insert_payment, call.bank, record.payment ...

        if outcome != "declined":
            outcome = "approved"
        payments_counter.add(1, {"status": "approved"})
        span.set_attribute("status", grpc_response.status)

    # Enregistrer la durée après le span (mesure le temps réel du span)
    duration_ms = (time.time() - start_time) * 1000
    payment_latency_histogram.record(duration_ms, {"outcome": outcome})

    return row_to_dict(payment), None
```

Cet histogramme sera côté **métier** (durée du processus métier), distincte de l'histogramme HTTP (qui inclut la sérialisation, le réseau, etc.) déjà fourni par Flask.

---

## Partie C — Distinction erreurs techniques vs erreurs métier

Un piège courant : marquer un "déclin bancaire" comme une **erreur HTTP 5xx** (ce qui trigger des alertes, pinging l'on-call). Mais c'est une **réponse métier valide**, pas un bug technique.

### C.1. Status HTTP cohérent avec la réalité

Modifier [payment-service/app.py](../../../src/payment-service/app.py) — s'assurer que les codes retour sont justes :

```python
@app.route("/payments", methods=["POST"])
def create_payment():
    data = request.get_json()
    if not data or not data.get("user_id") or data.get("amount") is None:
        # Erreur de validation client → 400 Bad Request
        return jsonify({"error": "user_id and amount are required"}), 400

    payment, error = process_payment(data["user_id"], data["amount"])
    if error and payment:
        # Refus bancaire → 402 Payment Required (résultat métier, pas un bug)
        # Le corps contient les données du paiement (status="declined", decline_reason) + la raison
        return jsonify({**payment, "error": error}), 402
    if error:
        # Erreur technique gRPC → 502 Bad Gateway ("le service derrière a crashé")
        return jsonify({"error": error, "payment_id": None}), 502

    return jsonify(payment), 201
```

Le span HTTP Flask enregistre `http.status_code = 402` pour les refus bancaires — **pas une erreur 5xx**, donc tes SLA restent propres. Le 502 reste réservé aux vrais problèmes techniques.

### C.2. Attributs métier pour catégoriser les déclines

Le span `process.payment` et ses enfants portent déjà les attributs clés. En Grafana, tu peux filtrer Tempo avec :

```
{ span.user_id = "john" }
{ span.bank_response.approved = "false" }
{ span.validation.status = "failed" }
```

Pour les déclines bancaires, `call.bank` expose via ses événements le `transaction_id` et le `status` retourné par la banque — sans avoir besoin d'un span supplémentaire.

---

## Partie D — Corréler traces, métriques, logs sur une même trace

C'est là que tout se noue : le même `trace_id` relie tous les signaux.

### D.1. Logs enrichis avec contexte OTel

Les logs sont déjà présents dans `service.py` à chaque étape critique et héritent automatiquement du `trace_id` du span courant (via le `LoggingInstrumentor` de l'exo 3) :

```python
def process_payment(user_id, amount):
    with tracer.start_as_current_span("process.payment") as span:

        with tracer.start_as_current_span("validate.payment") as val_span:
            if flags.get_boolean_value("paymentUnreachable", False):
                # log automatiquement taggé trace_id + span_id
                logger.warning("payment_unreachable_flag_active", extra={"user_id": user_id})
                raise PaymentUnreachableError(...)

        # log émis hors span enfant, toujours sous process.payment
        logger.info("payment_pending", extra={"payment_id": payment_id, "user_id": user_id, "amount": amount})

        with tracer.start_as_current_span("call.bank") as bank_span:
            except grpc.RpcError as e:
                logger.error("bank_transaction_rpc_error", extra={
                    "payment_id": payment_id,
                    "grpc_code": e.code().name if e.code() else "UNKNOWN",
                    "details": e.details(),
                })

        with tracer.start_as_current_span("record.payment"):
            logger.info("bank_transaction_completed", extra={
                "payment_id": payment_id,
                "transaction_id": grpc_response.transaction_id,
                "status": grpc_response.status,
            })
```

En Grafana Loki, tu peux faire `{trace_id="feeac4..."}` et tu verras tous les logs de cette trace.

### D.2. Vérifier en Grafana

1. **Tempo** : cherche une trace avec plusieurs spans `process.payment`, `validate.payment`, `call.bank`, `record.payment`.
2. **Prometheus** : visualise `rate(payments_total[5m])` et `rate(payments_declined_total[5m])` côte à côte.
3. **Loki** : cherche les logs avec le même `trace_id`.
4. Clique sur les **liens cross-signal** : Grafana corrèle automatiquement via `trace_id`.

---

## Analyse : la chaîne complète avec instrumentation custom

```text
┌─ Requête HTTP ────────────────────────────────────────────────┐
│                                                                 │
│  POST /payments {user_id, amount}                             │
│          │                                                      │
│          ├─ Flask auto-instrument        → span: POST /payments │
│          │                                                      │
│          ├─ service.process_payment()                         │
│          │   │                                                  │
│          │   ├─ span: process.payment                          │
│          │   │   attr: user_id, amount, status                │
│          │   │   │                                              │
│          │   │   ├─ span: validate.payment                     │
│          │   │   │   attr: validation.status                   │
│          │   │   │   event: validation_failed (si flag actif)  │
│          │   │   │                                              │
│          │   │   ├─ span: call.bank                            │
│          │   │   │   attr: bank_response.approved              │
│          │   │   │   event: bank_approved | bank_declined      │
│          │   │   │   │                                          │
│          │   │   │   └─ gRPC auto-instrument (client)          │
│          │   │   │       └─ bank-service: span (server)        │
│          │   │   │                                              │
│          │   │   └─ span: record.payment                       │
│          │   │       attr: payment_id, transaction_id          │
│          │   │                                                  │
│          │   └─ meter.record() : payment_processing_duration_ms│
│          │                                                      │
│          └─ HTTP 402 (Payment Required) or 201 (OK)            │
│                                                                 │
│  Tout ceci partage un trace_id.                               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘

↓ Collecteur OTel (processor resource/env, batch, memory_limiter)

┌─ Grafana (http://localhost:8080/grafana) ─────────────────────┐
│                                                                │
│  Tempo: Traces avec hiérarchie process.payment + bank tree    │
│  Prometheus: Counters, histograms                             │
│  Loki: Logs avec trace_id, tous sur la même requête           │
│                                                                │
│  Cross-linking: clique span → logs, metrics → traces          │
│                                                                │
└────────────────────────────────────────────────────────────────┘
```

Trois observations finales :

1. **Les spans custom s'imbriquent** dans l'arbre généré par auto-instrumentation. Pas de doublons, hiérarchie propre.
2. **Les événements** donnent de la granularité sans exploser le nombre de spans (10 déclines différents = 10 logs + 1 événement par span, pas 10 spans).
3. **Le même `trace_id` sur tous les signaux** = unicité. Une trace = un chemin logique métier, visible sur 3 backends différents.

## Critères de validation

- [ ] **Spans custom** : Un `POST /payments` génère au moins 5 spans (HTTP root + `process.payment` + `validate.payment` + `call.bank` + `record.payment`).
- [ ] **Attributs métier** : Chaque span `*.payment` / `call.bank` a des attributs (`user_id`, `amount`, `validation.status`, `bank_response.approved`…) visibles dans Tempo.
- [ ] **Événements** : Déplie `call.bank` et tu vois un événement `bank_approved` ou `bank_declined`.
- [ ] **Flag unreachable** : Active `paymentUnreachable` via le feature flag — Tempo doit montrer `validate.payment` avec l'événement `validation_failed` et `reason=paymentUnreachable`.
- [ ] **Compteurs** : `payments_total` et `payments_declined_total` existent et augmentent. Les attributs (`status`, `reason`) sont visibles en Prometheus.
- [ ] **Histogramme** : `payment_processing_duration_ms` est visible en Prometheus avec des quantiles (p50, p95, p99).
- [ ] **Logs corrélés** : Fais une requête, cherche le `trace_id` dans Loki. Tu trouves tous les logs de cette trace.
- [ ] **Status code** : Les déclines retournent HTTP 402, pas 502.
- [ ] **Cross-linking** : Dans Tempo, clique "Logs" en bas d'une trace → tu vois les logs Loki avec le même `trace_id`.

## Pour aller plus loin

- **Sampling** : ajouter un processor `tail_sampling` au collecteur pour garder 100 % des déclines et seulement 5 % des approbations. Voir [docs/03-observability.md](../../../03-observability.md).
- **Baggage** : utiliser `opentelemetry.baggage` pour propager des attributs métier à travers toute une requête sans les passer explicitement.
- **Autres types de spans** : spans **links** (une requête triggered par une autre), spans avec **kind=INTERNAL** vs **kind=SERVER**.
- **Métriques conditionnelles** : `payment_duration_by_tier` (premium vs standard) — c'est juste ajouter des attributs aux histogrammes.
- **SLOs** : « 95 % des paiements approved < 500ms » — requête Grafana sur `payment_processing_duration_ms`.

## Étape suivante

Ça y est ! Tu viens de compléter une **stack d'observabilité complète** :

- ✅ **Traces** (auto + custom) : `process.payment` divisé en étapes, contexte distribué Python → Go.
- ✅ **Métriques** (auto + custom) : compteurs métier, histogrammes de latence.
- ✅ **Logs** (corrélés) : chaque action loggée, reliée à sa trace.
- ✅ **Collecteur** : pipeline centralisé, enrichissement via processor `resource`, backends locaux Tempo/VictoriaMetrics/Loki.

Explore les dashboards Grafana et configure des alertes pour tes KPIs — tu as maintenant **toute l'observabilité** pour debugger en production.
