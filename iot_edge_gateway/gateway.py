from __future__ import annotations

import asyncio
import logging
from typing import Optional

from .config import EdgeGatewaySettings
from .hub import C2DCommandHandler, IoTHubClient
from .models import Alert, AlertSeverity, AlertType, DeviceReading, TelemetryBatch
from .processing import AlertManager, DataFilter, DataTranslator, DataValidator
from .protocols import BaseProtocol, MQTTProtocol, ModbusTCPProtocol, OPCUAProtocol

logger = logging.getLogger(__name__)


class EdgeGateway:
    """
    Main IoT Edge Gateway orchestrator.

    Pipeline per reading:
      Protocol → Translate → Filter → Validate → Alert → Batch → IoT Hub
                                                   ↓
                                             Local Alerts
    """

    def __init__(self, settings: Optional[EdgeGatewaySettings] = None) -> None:
        self.settings = settings or EdgeGatewaySettings()

        # Protocol adapters keyed by device_id
        self._protocols: dict[str, BaseProtocol] = {}

        # Processing pipeline components
        self._translator = DataTranslator()
        self._filter = DataFilter(
            default_deadband_percent=self.settings.gateway.deadband_percent
        )
        self._validator = DataValidator(thresholds=self.settings.thresholds)
        self._alert_manager = AlertManager(
            cooldown_seconds=self.settings.gateway.alert_cooldown_seconds
        )

        # IoT Hub client
        self._hub_client = IoTHubClient(self.settings.gateway)

        # C2D handler (protocols registered after protocol init)
        self._c2d_handler = C2DCommandHandler(
            protocols=self._protocols,
            timeout_seconds=self.settings.gateway.c2d_timeout_seconds,
        )

        # Telemetry batching
        self._pending_batch: list[DeviceReading] = []
        self._batch_lock = asyncio.Lock()
        self._batch_flush_task: Optional[asyncio.Task] = None
        self._running = False

        self._sequence = 0

        # Register the hub-forwarding alert handler immediately (not deferred to start())
        self._alert_manager.add_handler(self._on_alert)

    # ------------------------------------------------------------------
    # Startup / shutdown
    # ------------------------------------------------------------------

    async def start(self) -> None:
        logger.info("Starting Edge Gateway '%s'.", self.settings.gateway.gateway_id)
        self._running = True

        # Connect to IoT Hub
        try:
            await self._hub_client.connect()
            if self._hub_client.is_connected:
                await self._c2d_handler.attach_to_client(
                    self._hub_client._module_client or self._hub_client._client
                )
        except Exception as exc:
            logger.warning("IoT Hub connection failed at startup (will retry): %s", exc)

        # Initialise and start protocol adapters
        await self._init_protocols()

        # Start periodic batch flush
        self._batch_flush_task = asyncio.create_task(self._batch_flush_loop())

        logger.info("Edge Gateway started successfully.")

    async def stop(self) -> None:
        logger.info("Stopping Edge Gateway.")
        self._running = False

        # Stop all protocols
        for pid, proto in self._protocols.items():
            try:
                await proto.disconnect()
            except Exception as exc:
                logger.warning("Error disconnecting protocol %s: %s", pid, exc)

        # Flush remaining readings
        await self._flush_batch()

        if self._batch_flush_task:
            self._batch_flush_task.cancel()
            try:
                await self._batch_flush_task
            except asyncio.CancelledError:
                pass

        await self._hub_client.disconnect()
        logger.info("Edge Gateway stopped.")

    # ------------------------------------------------------------------
    # Protocol initialisation
    # ------------------------------------------------------------------

    async def _init_protocols(self) -> None:
        cfg = self.settings

        if cfg.gateway.enable_mqtt:
            proto = MQTTProtocol(config=cfg.mqtt, device_id="mqtt-gateway")
            await self._register_protocol("mqtt-gateway", proto)

        if cfg.gateway.enable_opcua:
            proto = OPCUAProtocol(config=cfg.opcua, device_id="opcua-device")
            await self._register_protocol("opcua-device", proto)

        if cfg.gateway.enable_modbus:
            proto = ModbusTCPProtocol(config=cfg.modbus, device_id="modbus-device")
            await self._register_protocol("modbus-device", proto)

    async def _register_protocol(self, device_id: str, protocol: BaseProtocol) -> None:
        protocol.set_data_callback(self._on_reading)
        try:
            await protocol.connect()
            await protocol.start_reading()
            self._protocols[device_id] = protocol
            logger.info("Protocol registered and started: %s", device_id)
        except Exception as exc:
            logger.error("Failed to start protocol for device %s: %s", device_id, exc)

    # ------------------------------------------------------------------
    # Data ingestion callback (called by each protocol adapter)
    # ------------------------------------------------------------------

    async def _on_reading(self, reading: DeviceReading) -> None:
        try:
            # Step 1: Translate raw → engineering units
            reading = self._translator.translate(reading)

            # Step 2: Filter (deadband, rate limiting, unimportant tags)
            reading = self._filter.filter(reading)

            # Step 3: Validate + generate alerts (always validate, even filtered)
            result = self._validator.validate(reading)
            if result.alerts:
                await self._alert_manager.process_alerts(result.alerts)

            # Step 4: Add to telemetry batch if it passed filtering
            if not reading.is_filtered:
                await self._enqueue_reading(reading)

        except Exception as exc:
            logger.error("Unhandled error in reading pipeline: %s", exc, exc_info=True)

    async def _enqueue_reading(self, reading: DeviceReading) -> None:
        async with self._batch_lock:
            self._pending_batch.append(reading)
            if len(self._pending_batch) >= self.settings.gateway.telemetry_batch_size:
                batch = self._take_batch()
            else:
                return
        await self._send_batch(batch)

    async def _batch_flush_loop(self) -> None:
        interval = self.settings.gateway.telemetry_batch_interval_seconds
        while self._running:
            try:
                await asyncio.sleep(interval)
                await self._flush_batch()
            except asyncio.CancelledError:
                break
            except Exception as exc:
                logger.error("Batch flush loop error: %s", exc, exc_info=True)

    async def _flush_batch(self) -> None:
        async with self._batch_lock:
            if not self._pending_batch:
                return
            batch = self._take_batch()
        await self._send_batch(batch)

    def _take_batch(self) -> TelemetryBatch:
        readings = self._pending_batch[:]
        self._pending_batch.clear()
        return TelemetryBatch(
            gateway_id=self.settings.gateway.gateway_id,
            readings=readings,
        )

    async def _send_batch(self, batch: TelemetryBatch) -> None:
        if not batch.readings:
            return
        logger.debug("Sending telemetry batch with %d readings.", len(batch.readings))
        success = await self._hub_client.send_telemetry(batch)
        if not success:
            logger.warning("Telemetry batch %s failed to enqueue for sending.", batch.batch_id)

    # ------------------------------------------------------------------
    # Alert handler
    # ------------------------------------------------------------------

    async def _on_alert(self, alert: Alert) -> None:
        # Always log; forward CRITICAL and above to IoT Hub
        if alert.severity in (AlertSeverity.CRITICAL, AlertSeverity.EMERGENCY):
            await self._hub_client.send_alert(alert)

    # ------------------------------------------------------------------
    # Public API for external configuration / testing
    # ------------------------------------------------------------------

    def add_translation_rule(self, rule: object) -> None:
        self._translator.add_rule(rule)  # type: ignore[arg-type]

    def add_filter_rule(self, rule: object) -> None:
        self._filter.add_rule(rule)  # type: ignore[arg-type]

    def add_threshold(self, threshold: object) -> None:
        self._validator.add_threshold(threshold)  # type: ignore[arg-type]

    def add_alert_handler(self, handler: object) -> None:
        self._alert_manager.add_handler(handler)  # type: ignore[arg-type]

    @property
    def active_alert_count(self) -> int:
        return self._alert_manager.get_alert_count()

    @property
    def critical_alert_count(self) -> int:
        return self._alert_manager.get_critical_alert_count()

    @property
    def hub_queue_size(self) -> int:
        return self._hub_client.queue_size

    @property
    def hub_circuit_state(self) -> str:
        return self._hub_client.circuit_state

    def get_active_alerts(
        self,
        min_severity: Optional[AlertSeverity] = None,
        device_id: Optional[str] = None,
    ) -> list[Alert]:
        return self._alert_manager.get_active_alerts(min_severity, device_id)
