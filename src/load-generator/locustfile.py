#!/usr/bin/python

import random
import uuid
import logging

from locust import HttpUser, task, between

from opentelemetry import context, baggage, propagate, trace
from opentelemetry.baggage.propagation import W3CBaggagePropagator
from opentelemetry.context import Context
from opentelemetry.metrics import set_meter_provider
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.system_metrics import SystemMetricsInstrumentor
from opentelemetry.instrumentation.urllib3 import URLLib3Instrumentor
from opentelemetry.instrumentation.logging import LoggingInstrumentor
from opentelemetry._logs import set_logger_provider
from opentelemetry.exporter.otlp.proto.grpc._log_exporter import OTLPLogExporter
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor

# Configure tracer provider
tracer_provider = TracerProvider()
trace.set_tracer_provider(tracer_provider)
tracer_provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter(insecure=True)))

# Configure logger provider
logger_provider = LoggerProvider()
set_logger_provider(logger_provider)
log_exporter = OTLPLogExporter(insecure=True)
logger_provider.add_log_record_processor(BatchLogRecordProcessor(log_exporter))
handler = LoggingHandler(level=logging.INFO, logger_provider=logger_provider)
root_logger = logging.getLogger()
root_logger.addHandler(handler)
root_logger.setLevel(logging.INFO)

# Configure metrics
metric_exporter = OTLPMetricExporter(insecure=True)
set_meter_provider(MeterProvider([PeriodicExportingMetricReader(metric_exporter)]))

# Ne propager que le baggage (pas le traceparent) pour que les services démarrent leurs propres traces racines
propagate.set_global_textmap(W3CBaggagePropagator())

# Instrument
LoggingInstrumentor().instrument(set_logging_format=True)
SystemMetricsInstrumentor().instrument()
URLLib3Instrumentor().instrument()

logging.info("Instrumentation complete")

CLIENT_USER = {"email": "john.doe@kraftr.tech", "password": "client123"}
ADMIN_USER = {"email": "admin@kraftr.tech", "password": "admin123"}


def _login(client, credentials):
    response = client.post("/api/users/login", json=credentials)
    if response.status_code == 200:
        return response.json().get("id")
    return None


def _fetch_product_ids(client, status="Active"):
    query = "" if status == "Active" else "?status=all"
    response = client.get(f"/api/products{query}")
    if response.status_code == 200:
        products = response.json()
        return [p["id"] for p in products if p.get("status") == status]
    return []


class WebsiteUser(HttpUser):
    wait_time = between(1, 10)
    weight = 9

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.tracer = trace.get_tracer(__name__)
        self.user_id = None
        self.product_ids = []

    def on_start(self):
        with self.tracer.start_as_current_span("user_session_start", context=Context()):
            session_id = str(uuid.uuid4())
            logging.info(f"Starting user session: {session_id}")
            ctx = baggage.set_baggage("session.id", session_id)
            ctx = baggage.set_baggage("synthetic_request", "true", context=ctx)
            context.attach(ctx)

            self.user_id = _login(self.client, CLIENT_USER)
            if self.user_id:
                logging.info(f"Client logged in: {self.user_id}")

            self.product_ids = _fetch_product_ids(self.client, "Active")
            logging.info(f"Loaded {len(self.product_ids)} active products")

    @task(5)
    def browse_products(self):
        with self.tracer.start_as_current_span("user_browse_products", context=Context()):
            logging.info("User browsing product catalog")
            self.client.get("/api/products")

    @task(10)
    def get_product(self):
        if not self.product_ids:
            return
        product_id = random.choice(self.product_ids)
        with self.tracer.start_as_current_span(
            "user_get_product", context=Context(), attributes={"product.id": product_id}
        ):
            logging.info(f"User viewing product: {product_id}")
            self.client.get(f"/api/products/{product_id}")

    @task(5)
    def view_cart(self):
        if not self.user_id:
            return
        with self.tracer.start_as_current_span(
            "user_view_cart", context=Context(), attributes={"user.id": self.user_id}
        ):
            logging.info(f"User viewing cart: {self.user_id}")
            self.client.get(f"/api/users/{self.user_id}/cart")

    @task(3)
    def add_to_cart(self):
        if not self.user_id or not self.product_ids:
            return
        product_id = random.choice(self.product_ids)
        quantity = random.choice([1, 2, 3])
        with self.tracer.start_as_current_span(
            "user_add_to_cart",
            context=Context(),
            attributes={"user.id": self.user_id, "product.id": product_id, "quantity": quantity},
        ):
            logging.info(f"User {self.user_id} adding {quantity}x {product_id} to cart")
            self.client.post(
                f"/api/users/{self.user_id}/cart",
                json={"product_id": product_id, "quantity": quantity},
            )

    @task(1)
    def checkout(self):
        if not self.user_id or not self.product_ids:
            return
        items = [
            {"product_id": random.choice(self.product_ids), "quantity": random.randint(1, 2)}
            for _ in range(random.randint(1, 3))
        ]
        with self.tracer.start_as_current_span(
            "user_checkout",
            context=Context(),
            attributes={"user.id": self.user_id, "item.count": len(items)},
        ):
            logging.info(f"User {self.user_id} checking out {len(items)} items")
            self.client.post(
                "/api/billing/checkout",
                json={"user_id": self.user_id, "items": items},
            )

    @task(2)
    def view_orders(self):
        if not self.user_id:
            return
        with self.tracer.start_as_current_span(
            "user_view_orders", context=Context(), attributes={"user.id": self.user_id}
        ):
            logging.info(f"User {self.user_id} viewing orders")
            self.client.get(f"/api/billing/orders/{self.user_id}")


class AdminUser(HttpUser):
    wait_time = between(3, 15)
    weight = 1

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.tracer = trace.get_tracer(__name__)
        self.admin_id = None
        self.all_product_ids = []

    def on_start(self):
        with self.tracer.start_as_current_span("admin_session_start", context=Context()):
            session_id = str(uuid.uuid4())
            logging.info(f"Starting admin session: {session_id}")
            ctx = baggage.set_baggage("session.id", session_id)
            ctx = baggage.set_baggage("synthetic_request", "true", context=ctx)
            context.attach(ctx)

            self.admin_id = _login(self.client, ADMIN_USER)
            if self.admin_id:
                logging.info(f"Admin logged in: {self.admin_id}")

            self.all_product_ids = _fetch_product_ids(self.client, "Active")
            logging.info(f"Admin loaded {len(self.all_product_ids)} products")

    @task(5)
    def view_all_products(self):
        with self.tracer.start_as_current_span("admin_view_all_products", context=Context()):
            logging.info("Admin listing all products")
            self.client.get("/api/products?status=all")

    @task(3)
    def view_users(self):
        with self.tracer.start_as_current_span("admin_view_users", context=Context()):
            logging.info("Admin listing users")
            self.client.get("/api/users")

    @task(5)
    def view_all_orders(self):
        with self.tracer.start_as_current_span("admin_view_all_orders", context=Context()):
            logging.info("Admin listing all orders")
            self.client.get("/api/billing/orders")

    @task(2)
    def update_stock(self):
        if not self.all_product_ids:
            return
        product_id = random.choice(self.all_product_ids)
        stock = random.randint(10, 100)
        with self.tracer.start_as_current_span(
            "admin_update_stock",
            context=Context(),
            attributes={"product.id": product_id, "stock": stock},
        ):
            logging.info(f"Admin updating stock for {product_id}: {stock}")
            self.client.patch(
                f"/api/products/{product_id}/stock",
                json={"stock": stock},
            )

    @task(1)
    def edit_product(self):
        if not self.all_product_ids:
            return
        product_id = random.choice(self.all_product_ids)
        with self.tracer.start_as_current_span(
            "admin_edit_product", context=Context(), attributes={"product.id": product_id}
        ):
            response = self.client.get(f"/api/products/{product_id}")
            if response.status_code != 200:
                return
            product = response.json()
            logging.info(f"Admin editing product: {product_id}")
            self.client.put(
                f"/api/products/{product_id}",
                json={
                    "name": product.get("name"),
                    "description": product.get("description", ""),
                    "price": product.get("price"),
                    "stock": product.get("stock"),
                    "status": product.get("status"),
                    "category": product.get("category", ""),
                    "image_url": product.get("image_url", ""),
                },
            )
