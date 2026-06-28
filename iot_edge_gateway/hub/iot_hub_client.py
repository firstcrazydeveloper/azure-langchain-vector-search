from __future__ import annotations

import asyncio
import json
import logging
import time
from typing import Any, Optional

from ..config import GatewayConfig
from ..models import Alert, TelemetryBatch
from ..utils import CircuitBreaker, CircuitBreakerOpenError, RetryConfig, retry_async

logger = logging.getLogger(__name__)

_MAX_IOTHUB_MESSAGE_BYTES = 262144   # 256 KB hard limit
_IOTHUB_THROTTLE_LIMIT = 100        # messages per minute per device (D1 tier)


class IoTHubClient:
    """
    Sends telemetry batches and alerts to Azure IoT Hub.
    Supports both direct IoT Hub device connection and IoT Edge module connection.
    """

    def __init__(self, config: GatewayConfig) -> None:
        self.config = config
        self._client: Optional[Any] = None
        self._module_client: Optional[Any] = None
        self._connected = False
        self._sequence_number = 0
        self._send_count = 0
        self._send_window_start = time.monotonic()
        self._circuit_breaker = CircuitBreaker(
            name="iothub",
            failure_threshold=5,
            recovery_timeout=60.0,
            success_threshold=2,
        )
        self._retry_config = RetryConfig(
            max_attempts=4,
            base_delay_seconds=2.0,
            max_delay_seconds=30.0,
            exponential_base=2.0,
        )
        self._send_queue: asyncio.Queue[dict[str, Any]] = asyncio.Queue(
            maxsize=config.max_queue_size
        )
        self._sender_task: Optional[asyncio.Task] = None

    async def connect(self) -> None:
        if self.config.iothub_module_connection_string:
            await self._connect_as_module()
        elif self.config.iothub_connection_string:
            await self._connect_as_device()
        else:
            logger.warning("No IoT Hub connection string configured. Running in offline mode.")
            self._connected = False
            return

        self._sender_task = asyncio.create_task(self._queue_sender_loop())
        logger.info("IoT Hub sender queue started.")

    async def _connect_as_module(self) -> None:
        try:
            from azure.iot.device.aio import IoTHubModuleClient
        except ImportError as exc:
            raise RuntimeError("azure-iot-device not installed. Run: pip install azure-iot-device") from exc
        try:
            client = IoTHubModuleClient.create_from_connection_string(
                self.config.iothub_module_connection_string
            )
            await client.connect()
            self._module_client = client
            self._connected = True
            logger.info("Connected to IoT Hub as Edge module.")
        except Exception as exc:
            logger.error("IoT Hub module connect failed: %s", exc)
            raise

    async def _connect_as_device(self) -> None:
        try:
            from azure.iot.device.aio import IoTHubDeviceClient
        except ImportError as exc:
            raise RuntimeError("azure-iot-device not installed. Run: pip install azure-iot-device") from exc
        try:
            client = IoTHubDeviceClient.create_from_connection_string(
                self.config.iothub_connection_string
            )
            await client.connect()
            self._client = client
            self._connected = True
            logger.info("Connected to IoT Hub as device.")
        except Exception as exc:
            logger.error("IoT Hub device connect failed: %s", exc)
            raise

    async def disconnect(self) -> None:
        if self._sender_task:
            self._sender_task.cancel()
            try:
                await self._sender_task
            except asyncio.CancelledError:
                pass
        if self._module_client:
            await self._module_client.disconnect()
        if self._client:
            await self._client.disconnect()
        self._connected = False
        logger.info("IoT Hub disconnected.")

    async def send_telemetry(self, batch: TelemetryBatch) -> bool:
        if not self._connected:
            logger.warning("IoT Hub not connected — dropping telemetry batch %s.", batch.batch_id)
            return False

        self._sequence_number += 1
        batch.sequence_number = self._sequence_number
        payload = json.dumps(batch.to_iothub_message())

        if len(payload.encode("utf-8")) > _MAX_IOTHUB_MESSAGE_BYTES:
            logger.warning(
                "Telemetry batch %s exceeds %dB limit (%dB). Splitting.",
                batch.batch_id, _MAX_IOTHUB_MESSAGE_BYTES, len(payload.encode()),
            )
            return await self._send_split_batch(batch)

        try:
            self._send_queue.put_nowait({"type": "telemetry", "payload": payload, "batch_id": batch.batch_id})
            return True
        except asyncio.QueueFull:
            logger.error("IoT Hub send queue full — dropping telemetry batch %s.", batch.batch_id)
            return False

    async def send_alert(self, alert: Alert) -> bool:
        if not self._connected:
            logger.warning("IoT Hub not connected — cannot send alert %s.", alert.alert_id)
            return False
        payload = json.dumps(alert.to_dict())
        try:
            self._send_queue.put_nowait({"type": "alert", "payload": payload, "alert_id": alert.alert_id})
            return True
        except asyncio.QueueFull:
            logger.error("IoT Hub queue full — dropping alert %s.", alert.alert_id)
            return False

    async def _queue_sender_loop(self) -> None:
        while True:
            try:
                item = await asyncio.wait_for(self._send_queue.get(), timeout=1.0)
                await self._send_with_retry(item)
                self._send_queue.task_done()
            except asyncio.TimeoutError:
                pass
            except asyncio.CancelledError:
                break
            except Exception as exc:
                logger.error("Sender loop unexpected error: %s", exc, exc_info=True)

    async def _send_with_retry(self, item: dict[str, Any]) -> None:
        async def _do_send() -> None:
            self._check_rate_limit()
            payload = item["payload"]
            msg_type = item.get("type", "telemetry")
            await self._circuit_breaker.call(self._do_iothub_send, payload, msg_type)
            self._send_count += 1

        try:
            await retry_async(_do_send, self._retry_config)
        except CircuitBreakerOpenError as exc:
            logger.error("IoT Hub circuit open — message dropped: %s", exc)
        except Exception as exc:
            logger.error("IoT Hub send failed after retries: %s", exc)

    async def _do_iothub_send(self, payload: str, msg_type: str) -> None:
        try:
            from azure.iot.device import Message
        except ImportError as exc:
            raise RuntimeError("azure-iot-device not installed.") from exc

        msg = Message(payload)
        msg.content_type = "application/json"
        msg.content_encoding = "utf-8"
        msg.custom_properties["messageType"] = msg_type
        msg.custom_properties["gatewayId"] = self.config.gateway_id

        active_client = self._module_client or self._client
        if not active_client:
            raise RuntimeError("No active IoT Hub client.")
        await active_client.send_message(msg)

    async def _send_split_batch(self, batch: TelemetryBatch) -> bool:
        mid = len(batch.readings) // 2
        if mid == 0:
            logger.error("Single reading exceeds message size limit — cannot split further.")
            return False

        from ..models import TelemetryBatch as TB
        first = TB(gateway_id=batch.gateway_id, readings=batch.readings[:mid])
        second = TB(gateway_id=batch.gateway_id, readings=batch.readings[mid:])
        r1 = await self.send_telemetry(first)
        r2 = await self.send_telemetry(second)
        return r1 and r2

    def _check_rate_limit(self) -> None:
        now = time.monotonic()
        elapsed = now - self._send_window_start
        if elapsed >= 60.0:
            self._send_count = 0
            self._send_window_start = now
        if self._send_count >= _IOTHUB_THROTTLE_LIMIT:
            wait = 60.0 - elapsed
            if wait > 0:
                raise RuntimeError(f"IoT Hub rate limit: wait {wait:.1f}s before next send.")

    @property
    def queue_size(self) -> int:
        return self._send_queue.qsize()

    @property
    def is_connected(self) -> bool:
        return self._connected

    @property
    def circuit_state(self) -> str:
        return self._circuit_breaker.state.value
