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

logger = logging.getLogger("payments.service")


class PaymentUnreachableError(RuntimeError):
    """Le service paiement refuse les requetes (flag paymentUnreachable=on)."""


class PaymentSimulatedFailureError(RuntimeError):
    """Echec simule via le flag paymentFailure."""

BANK_SERVICE_HOST = os.environ.get("BANK_SERVICE_HOST", "bank-service:50051")


def row_to_dict(row):
    return dict(row)


def process_payment(user_id, amount):
    flags = client()

    if flags.get_boolean_value("paymentUnreachable", False):
        raise PaymentUnreachableError("payment service is configured as unreachable")

    fail_rate = flags.get_float_value("paymentFailure", 0.0)
    if fail_rate > 0 and random.random() < fail_rate:
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

    payment_id = insert_payment(user_id, amount)
    logger.info(
        "payment_pending",
        extra={"payment_id": payment_id, "user_id": user_id, "amount": amount},
    )

    try:
        channel = grpc.insecure_channel(BANK_SERVICE_HOST)
        stub = transaction_pb2_grpc.TransactionServiceStub(channel)
        grpc_request = transaction_pb2.TransactionRequest(
            merchant_id="atelier-store",
            amount=amount,
        )
        grpc_response = stub.ProcessTransaction(grpc_request, timeout=10)

        update_payment_status(
            payment_id,
            grpc_response.status,
            grpc_response.transaction_id,
            grpc_response.decline_reason or None,
        )

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


        payment = find_payment_by_id(payment_id)

        if grpc_response.status == "declined":
            return row_to_dict(payment), grpc_response.decline_reason or "unknown"

        return row_to_dict(payment), None

    except grpc.RpcError as e:
        update_payment_status(payment_id, "failed")
        logger.error(
            "bank_transaction_rpc_error",
            extra={
                "payment_id": payment_id,
                "grpc_code": e.code().name if e.code() else "UNKNOWN",
                "details": e.details(),
            },
        )
        return None, f"bank transaction failed: {e.details()}"


def get_payment(payment_id):
    payment = find_payment_by_id(payment_id)
    if not payment:
        return None
    return row_to_dict(payment)


def get_all_payments():
    return [row_to_dict(p) for p in list_all_payments()]
