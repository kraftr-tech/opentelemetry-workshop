# SPDX-FileCopyrightText: 2026 Cedric Moulard / Kraftr
# SPDX-License-Identifier: MIT

import os
import logging

from openfeature import api
from openfeature.contrib.provider.flagd import FlagdProvider

logger = logging.getLogger("feature_flags")


def init_flags():
    host = os.getenv("FLAGD_HOST", "flagd")
    port = int(os.getenv("FLAGD_PORT", "8013"))
    api.set_provider(FlagdProvider(host=host, port=port))
    logger.info("flagd_provider_registered", extra={"host": host, "port": port})


def client():
    return api.get_client()
