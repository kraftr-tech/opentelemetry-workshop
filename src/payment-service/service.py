# SPDX-FileCopyrightText: 2026 Cédric Moulard / Kraftr
# SPDX-License-Identifier: MIT

import logging
import os
import random
import time

import grpc
import transaction_pb2
import transaction_pb2_grpc

from db import (
    insert_payment,
    update_payment_status,
    find_payment_by_id,
    list_all_payments,
)
from feature_flags import client
from opentelemetry import metrics, trace

logger = logging.getLogger("payments.service")
tracer = trace.get_tracer(__name__)
meter = metrics.get_meter(__name__)

PAYMENTS_TOTAL = meter.create_counter(
    "payments.total", description="Total payment requests"
)
PAYMENTS_DECLINED = meter.create_counter(
    "payments_declined.total", description="Declined payments"
)
PAYMENT_DURATION = meter.create_histogram(
    "payment.duration", unit="ms", description="Payment processing duration"
)

class PaymentUnreachableError(RuntimeError):
    """Le service paiement refuse les requetes (flag paymentUnreachable=on)."""


class PaymentSimulatedFailureError(RuntimeError):
    """Echec simule via le flag paymentFailure."""

BANK_SERVICE_HOST = os.environ.get("BANK_SERVICE_HOST", "bank-service:50051")


def row_to_dict(row):
    return dict(row)


def process_payment(user_id, amount):
    start = time.perf_counter()
    with tracer.start_as_current_span("process.payment") as span:
        span.set_attribute("user_id", user_id)
        span.set_attribute("amount", amount)
        PAYMENTS_TOTAL.add(1, {"status": "attempted"})

        with tracer.start_as_current_span("validate.payment") as val_span:
            flags = client()

            if flags.get_boolean_value("paymentUnreachable", False):
                val_span.set_attribute("validation.status", "failed")
                val_span.add_event("validation_failed", {"reason": "paymentUnreachable"})
                PAYMENTS_DECLINED.add(1, {"reason": "unreachable"})
                PAYMENTS_TOTAL.add(1, {"status": "declined"})
                PAYMENT_DURATION.record(
                    (time.perf_counter() - start) * 1000, {"outcome": "unreachable"}
                )
                raise PaymentUnreachableError("payment service is configured as unreachable")

            fail_rate = flags.get_float_value("paymentFailure", 0.0)
            if fail_rate > 0 and random.random() < fail_rate:
                val_span.set_attribute("validation.status", "failed")
                val_span.add_event("validation_failed", {"reason": "simulated_failure", "fail_rate": fail_rate})
                PAYMENTS_DECLINED.add(1, {"reason": "simulated_failure"})
                PAYMENTS_TOTAL.add(1, {"status": "declined"})
                PAYMENT_DURATION.record(
                    (time.perf_counter() - start) * 1000, {"outcome": "simulated_failure"}
                )
                raise PaymentSimulatedFailureError(
                    f"simulated charge failure (rate={fail_rate})"
                )

            delay_ms = flags.get_integer_value("paymentSlow", 0)
            if delay_ms > 0:
                logger.info(
                    "payment_slow_delay_applied",
                    extra={"delay_ms": delay_ms, "user_id": user_id},
                )
                time.sleep(delay_ms / 1000)

            val_span.set_attribute("validation.status", "passed")

        payment_id = insert_payment(user_id, amount)
        logger.info(
            "payment_pending",
            extra={"payment_id": payment_id, "user_id": user_id, "amount": amount},
        )

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
                logger.error(
                    "bank_transaction_rpc_error",
                    extra={
                        "payment_id": payment_id,
                        "grpc_code": e.code().name if e.code() else "UNKNOWN",
                        "details": e.details(),
                    },
                )
                PAYMENTS_DECLINED.add(1, {"reason": "bank_unavailable"})
                PAYMENTS_TOTAL.add(1, {"status": "declined"})
                PAYMENT_DURATION.record(
                    (time.perf_counter() - start) * 1000, {"outcome": "bank_unavailable"}
                )
                return None, f"bank transaction failed: {e.details()}"

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
                logger.warning(
                    "bank_transaction_completed",
                    extra={
                        "payment_id": payment_id,
                        "transaction_id": grpc_response.transaction_id,
                        "status": grpc_response.status,
                        "reason": reason,
                    },
                )
            else:
                logger.info(
                    "bank_transaction_completed",
                    extra={
                        "payment_id": payment_id,
                        "transaction_id": grpc_response.transaction_id,
                        "status": grpc_response.status,
                    },
                )

            record_span.set_attribute("payment_id", payment_id)
            record_span.set_attribute("transaction_id", grpc_response.transaction_id)
            record_span.set_attribute("recorded", True)

        span.set_attribute("status", grpc_response.status)

        outcome = "approved" if grpc_response.status == "approved" else "bank_declined"
        if grpc_response.status != "approved":
            PAYMENTS_DECLINED.add(1, {"reason": "bank_declined"})
        PAYMENTS_TOTAL.add(1, {"status": grpc_response.status})
        PAYMENT_DURATION.record(
            (time.perf_counter() - start) * 1000, {"outcome": outcome}
        )

        if grpc_response.status == "declined":
            return row_to_dict(payment), grpc_response.decline_reason or "unknown"

        return row_to_dict(payment), None


def get_payment(payment_id):
    payment = find_payment_by_id(payment_id)
    if not payment:
        return None
    return row_to_dict(payment)


def get_all_payments():
    return [row_to_dict(p) for p in list_all_payments()]
