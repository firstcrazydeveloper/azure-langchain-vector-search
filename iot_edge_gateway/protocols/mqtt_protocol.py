from __future__ import annotations

import asyncio
import json
import logging
import ssl
from datetime import datetime, timezone
from typing import Any, Optional

from ..config import MQTTConfig
from ..models import DeviceReading, DataQuality, ProtocolType
from .base_protocol import BaseProtocol, ProtocolError

logger = logging.getLogger(__name__)


class MQTTProtocol(BaseProtocol):
    """MQTT protocol adapter using asyncio-compatible paho wrapper."""

    def __init__(self, config: MQTTConfig, device_id: str = "mqtt-gateway") -> None:
        super().__init__(device_id)
        self.config = config
        self._client: Optional[Any] = None
        self._message_queue: asyncio.Queue[dict[str, Any]] = asyncio.Queue(maxsize=10000)
        self._reader_task: Optional[asyncio.Task] = None

    async def connect(self) -> None:
        try:
            import paho.mqtt.client as mqtt
        except ImportError as exc:
            raise ProtocolError("paho-mqtt is not installed. Run: pip install paho-mqtt") from exc

        client = mqtt.Client(client_id=self.config.client_id, clean_session=True)

        if self.config.username:
            client.username_pw_set(self.config.username, self.config.password or None)

        if self.config.tls_enabled:
            tls_ctx = ssl.create_default_context(ssl.Purpose.SERVER_AUTH)
            if self.config.ca_cert_path:
                tls_ctx.load_verify_locations(cafile=self.config.ca_cert_path)
            if self.config.client_cert_path and self.config.client_key_path:
                tls_ctx.load_cert_chain(
                    certfile=self.config.client_cert_path,
                    keyfile=self.config.client_key_path,
                )
            client.tls_set_context(tls_ctx)

        loop = asyncio.get_event_loop()

        def on_connect(client: Any, userdata: Any, flags: Any, rc: int) -> None:
            if rc == 0:
                logger.info("MQTT connected to %s:%d", self.config.broker_host, self.config.broker_port)
                for topic in self.config.topics:
                    client.subscribe(topic, qos=self.config.qos)
                    logger.info("MQTT subscribed to topic: %s", topic)
                self._connected = True
            else:
                logger.error("MQTT connection refused, return code: %d", rc)
                self._connected = False

        def on_disconnect(client: Any, userdata: Any, rc: int) -> None:
            self._connected = False
            logger.warning("MQTT disconnected (rc=%d). Will attempt reconnect.", rc)

        def on_message(client: Any, userdata: Any, msg: Any) -> None:
            try:
                payload_str = msg.payload.decode("utf-8", errors="replace")
                loop.call_soon_threadsafe(
                    self._message_queue.put_nowait,
                    {"topic": msg.topic, "payload": payload_str, "qos": msg.qos},
                )
            except asyncio.QueueFull:
                logger.warning("MQTT message queue full — dropping message from topic %s.", msg.topic)
            except Exception as exc:
                logger.error("MQTT on_message error: %s", exc, exc_info=True)

        client.on_connect = on_connect
        client.on_disconnect = on_disconnect
        client.on_message = on_message

        try:
            client.connect(
                self.config.broker_host,
                self.config.broker_port,
                keepalive=self.config.keepalive_seconds,
            )
        except (OSError, ConnectionRefusedError) as exc:
            raise ProtocolError(f"MQTT connect failed: {exc}") from exc

        client.loop_start()
        self._client = client

        # Wait up to 10s for connection
        for _ in range(100):
            if self._connected:
                return
            await asyncio.sleep(0.1)
        raise ProtocolError("MQTT connection timed out after 10 seconds.")

    async def disconnect(self) -> None:
        self._running = False
        if self._reader_task:
            self._reader_task.cancel()
            try:
                await self._reader_task
            except asyncio.CancelledError:
                pass
        if self._client:
            self._client.loop_stop()
            self._client.disconnect()
        self._connected = False
        logger.info("MQTT disconnected.")

    async def start_reading(self) -> None:
        self._running = True
        self._reader_task = asyncio.create_task(self._process_messages())
        logger.info("MQTT reading started.")

    async def _process_messages(self) -> None:
        while self._running:
            try:
                msg = await asyncio.wait_for(self._message_queue.get(), timeout=1.0)
                reading = self._parse_message(msg)
                if reading:
                    await self._emit(reading)
            except asyncio.TimeoutError:
                if not self._connected and self._running:
                    try:
                        await self.reconnect(
                            max_attempts=self.config.max_reconnect_attempts,
                            delay=self.config.reconnect_delay_seconds,
                        )
                    except ProtocolError as exc:
                        logger.critical("MQTT permanent connection failure: %s", exc)
                        self._running = False
            except asyncio.CancelledError:
                break
            except Exception as exc:
                logger.error("MQTT message processing error: %s", exc, exc_info=True)

    def _parse_message(self, msg: dict[str, Any]) -> Optional[DeviceReading]:
        topic: str = msg["topic"]
        payload: str = msg["payload"]

        if not payload or not payload.strip():
            logger.debug("Empty MQTT payload on topic %s — skipping.", topic)
            return None

        parts = topic.split("/")
        device_id = parts[1] if len(parts) > 1 else self.device_id
        tag_name = "/".join(parts[2:]) if len(parts) > 2 else "unknown"

        quality = DataQuality.GOOD
        raw_value: Any = payload

        try:
            data = json.loads(payload)
            if isinstance(data, dict):
                raw_value = data.get("value", data.get("v", data))
                tag_name = data.get("tag", data.get("name", tag_name))
                quality_str = data.get("quality", data.get("q", "GOOD")).upper()
                quality = DataQuality(quality_str) if quality_str in DataQuality.__members__ else DataQuality.UNCERTAIN
                src_ts_str = data.get("timestamp", data.get("ts"))
                src_ts = _parse_iso_timestamp(src_ts_str)
            else:
                raw_value = data
                src_ts = None
        except (json.JSONDecodeError, ValueError):
            # Treat raw payload as scalar value
            raw_value = _coerce_scalar(payload)
            src_ts = None

        translated = _to_float(raw_value)

        try:
            return DeviceReading(
                device_id=device_id,
                protocol=ProtocolType.MQTT,
                tag_name=tag_name,
                raw_value=raw_value,
                translated_value=translated,
                quality=quality,
                source_timestamp=src_ts,
                metadata={"topic": topic, "qos": msg.get("qos", 0)},
            )
        except Exception as exc:
            logger.warning("Failed to create DeviceReading from MQTT message: %s", exc)
            return None

    async def write_value(self, tag_name: str, value: object) -> bool:
        if not self._client or not self._connected:
            logger.error("MQTT not connected — cannot write to %s.", tag_name)
            return False
        payload = json.dumps({"value": value, "timestamp": datetime.now(timezone.utc).isoformat()})
        topic = f"devices/{self.device_id}/commands/{tag_name}"
        result = self._client.publish(topic, payload, qos=self.config.qos)
        if result.rc == 0:
            logger.info("MQTT published to %s: %s", topic, payload)
            return True
        logger.error("MQTT publish failed (rc=%d) on topic %s.", result.rc, topic)
        return False


def _parse_iso_timestamp(ts_str: Any) -> Optional[datetime]:
    if not ts_str:
        return None
    try:
        dt = datetime.fromisoformat(str(ts_str).replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except (ValueError, TypeError):
        return None


def _coerce_scalar(value: str) -> Any:
    for converter in (int, float):
        try:
            return converter(value)
        except (ValueError, TypeError):
            pass
    if value.lower() in ("true", "on", "1"):
        return True
    if value.lower() in ("false", "off", "0"):
        return False
    return value


def _to_float(value: Any) -> Optional[float]:
    if value is None:
        return None
    if isinstance(value, bool):
        return float(value)
    try:
        import math
        f = float(value)
        if math.isnan(f) or math.isinf(f):
            return None
        return f
    except (TypeError, ValueError):
        return None
