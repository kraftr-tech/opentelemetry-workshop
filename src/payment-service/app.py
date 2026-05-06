# SPDX-FileCopyrightText: 2026 Cédric Moulard / Kraftr
# SPDX-License-Identifier: MIT

import logging

from flask import Flask, request, jsonify
from flask_cors import CORS
from opentelemetry.instrumentation.flask import FlaskInstrumentor
from opentelemetry.instrumentation.grpc import GrpcInstrumentorClient
from opentelemetry.instrumentation.sqlite3 import SQLite3Instrumentor

from db import init_db, close_db
from otel import setup_tracing, setup_metrics
import service
from feature_flags import init_flags
from service import PaymentUnreachableError, PaymentSimulatedFailureError

setup_tracing()
setup_metrics()
GrpcInstrumentorClient().instrument()
SQLite3Instrumentor().instrument()

logger = logging.getLogger("payments")

app = Flask(__name__)
FlaskInstrumentor().instrument_app(app)
CORS(app)
app.teardown_appcontext(close_db)


@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok"})


@app.route("/payments", methods=["POST"])
def create_payment():
    data = request.get_json()
    if not data or not data.get("user_id") or data.get("amount") is None:
        logger.warning("create_payment_invalid_payload")
        return jsonify({"error": "user_id and amount are required"}), 400

    try:
        payment, error = service.process_payment(data["user_id"], data["amount"])
    except PaymentUnreachableError as e:
        logger.error(
            "payment_unreachable",
            extra={"user_id": data["user_id"], "amount": data["amount"]},
        )
        return jsonify({"error": str(e)}), 503
    except PaymentSimulatedFailureError as e:
        logger.error(
            "payment_simulated_failure",
            extra={"user_id": data["user_id"], "amount": data["amount"]},
        )
        return jsonify({"error": str(e)}), 500
    if error and payment:
        logger.warning(
            "payment_bank_declined",
            extra={"user_id": data["user_id"], "amount": data["amount"], "decline_reason": error},
        )
        return jsonify({**payment, "error": error}), 402
    if error:
        logger.warning(
            "payment_bank_failure",
            extra={"user_id": data["user_id"], "amount": data["amount"], "reason": error},
        )
        return jsonify({"error": error, "payment_id": None}), 502
    logger.info(
        "payment_created",
        extra={
            "payment_id": payment["id"],
            "user_id": payment["user_id"],
            "amount": payment["amount"],
            "status": payment["status"],
        },
    )
    return jsonify(payment), 201


@app.route("/payments", methods=["GET"])
def list_payments():
    payments = service.get_all_payments()
    logger.info("payments_listed", extra={"result_count": len(payments)})
    return jsonify(payments), 200


@app.route("/payments/<payment_id>", methods=["GET"])
def get_payment(payment_id):
    payment = service.get_payment(payment_id)
    if not payment:
        logger.warning("payment_not_found", extra={"payment_id": payment_id})
        return jsonify({"error": "payment not found"}), 404
    return jsonify(payment), 200


init_flags()
init_db(app)

logger.info("service_started", extra={"port": 8003})


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=8003)
