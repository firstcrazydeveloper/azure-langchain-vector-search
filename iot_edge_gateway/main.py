#!/usr/bin/env python3
"""
Entry point for the IoT Edge Gateway module.
Reads configuration from environment variables and starts the gateway.
"""
from __future__ import annotations

import asyncio
import logging
import os
import signal
import sys

from .config import EdgeGatewaySettings
from .gateway import EdgeGateway
from .processing import DataFilter, DataTranslator
from .processing.filter import FilterRule
from .processing.translator import TranslationRule


def _configure_logging(level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, level, logging.INFO),
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        stream=sys.stdout,
    )


def _apply_demo_rules(gateway: EdgeGateway) -> None:
    """Apply example translation and filter rules. Replace with real configuration."""
    # Modbus temperature register: raw 0-4095 → 0-150°C
    gateway.add_translation_rule(TranslationRule(
        tag_pattern="holding_40001",
        scale=150.0 / 4095.0,
        unit="°C",
        device_name="Furnace-1",
    ))
    # Modbus pressure register: raw 0-4095 → 0-10 bar
    gateway.add_translation_rule(TranslationRule(
        tag_pattern="holding_40002",
        scale=10.0 / 4095.0,
        unit="bar",
        device_name="Furnace-1",
        clamp_min=0.0,
        clamp_max=10.0,
    ))
    # OPC UA speed node: direct float, unit rpm
    gateway.add_translation_rule(TranslationRule(
        tag_pattern="ns=2;i=*",
        scale=1.0,
        unit="rpm",
    ))

    # Filter rules
    gateway.add_filter_rule(FilterRule(
        tag_pattern="holding_*",
        deadband_percent=0.5,
        min_send_interval_seconds=2.0,
    ))
    gateway.add_filter_rule(FilterRule(
        tag_pattern="devices/*/diagnostics/*",
        important=False,
    ))


async def _run() -> None:
    settings = EdgeGatewaySettings()
    _configure_logging(settings.gateway.log_level)
    logger = logging.getLogger(__name__)

    gateway = EdgeGateway(settings)
    _apply_demo_rules(gateway)

    loop = asyncio.get_running_loop()
    shutdown_event = asyncio.Event()

    def _handle_signal() -> None:
        logger.info("Shutdown signal received.")
        shutdown_event.set()

    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, _handle_signal)

    await gateway.start()
    logger.info("IoT Edge Gateway running. Press Ctrl+C to stop.")

    await shutdown_event.wait()
    await gateway.stop()
    logger.info("IoT Edge Gateway shut down cleanly.")


def main() -> None:
    asyncio.run(_run())


if __name__ == "__main__":
    main()
