from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timezone
from typing import Any, Optional

from ..config import OPCUAConfig
from ..models import DeviceReading, DataQuality, ProtocolType
from .base_protocol import BaseProtocol, ProtocolError

logger = logging.getLogger(__name__)

# OPC UA status codes — good range is 0x00000000 – 0x3FFFFFFF
_OPCUA_BAD_STATUS_MASK = 0xC0000000
_OPCUA_UNCERTAIN_STATUS_MASK = 0x40000000


class OPCUADataChangeHandler:
    """Subscription handler for OPC UA monitored item data changes."""

    def __init__(self, protocol: "OPCUAProtocol") -> None:
        self._protocol = protocol

    def datachange_notification(self, node: Any, val: Any, data: Any) -> None:
        loop = asyncio.get_event_loop()
        loop.call_soon_threadsafe(
            asyncio.create_task,
            self._protocol._handle_datachange(node, val, data),
        )

    def event_notification(self, event: Any) -> None:
        logger.debug("OPC UA event: %s", event)

    def status_change_notification(self, status: Any) -> None:
        logger.info("OPC UA subscription status changed: %s", status)


class OPCUAProtocol(BaseProtocol):
    """OPC UA protocol adapter using asyncua library."""

    def __init__(self, config: OPCUAConfig, device_id: str = "opcua-device") -> None:
        super().__init__(device_id)
        self.config = config
        self._client: Optional[Any] = None
        self._subscription: Optional[Any] = None
        self._nodes: dict[str, Any] = {}
        self._keep_alive_task: Optional[asyncio.Task] = None

    async def connect(self) -> None:
        try:
            from asyncua import Client
            from asyncua.crypto.security_policies import SecurityPolicyBasic256Sha256
        except ImportError as exc:
            raise ProtocolError("asyncua is not installed. Run: pip install asyncua") from exc

        client = Client(url=self.config.endpoint_url)
        client.set_session_timeout(self.config.session_timeout_ms)

        if self.config.username:
            client.set_user(self.config.username)
            client.set_password(self.config.password)

        if self.config.security_policy not in ("NoSecurity", "None", ""):
            if not self.config.certificate_path or not self.config.private_key_path:
                raise ProtocolError("OPC UA security requires certificate_path and private_key_path.")
            await client.set_security_string(
                f"{self.config.security_policy},{self.config.security_mode},"
                f"{self.config.certificate_path},{self.config.private_key_path}"
            )

        try:
            await client.connect()
        except Exception as exc:
            raise ProtocolError(f"OPC UA connect failed to {self.config.endpoint_url}: {exc}") from exc

        self._client = client
        self._connected = True
        logger.info("OPC UA connected to %s.", self.config.endpoint_url)

        await self._resolve_nodes()

    async def _resolve_nodes(self) -> None:
        if not self._client:
            return
        self._nodes.clear()
        for node_id in self.config.node_ids:
            try:
                node = self._client.get_node(node_id)
                await node.get_browse_name()  # validate node exists
                self._nodes[node_id] = node
                logger.debug("OPC UA resolved node: %s", node_id)
            except Exception as exc:
                logger.warning("OPC UA could not resolve node %s: %s", node_id, exc)

    async def disconnect(self) -> None:
        self._running = False
        if self._keep_alive_task:
            self._keep_alive_task.cancel()
            try:
                await self._keep_alive_task
            except asyncio.CancelledError:
                pass
        if self._subscription:
            try:
                await self._subscription.delete()
            except Exception:
                pass
            self._subscription = None
        if self._client:
            try:
                await self._client.disconnect()
            except Exception:
                pass
            self._client = None
        self._connected = False
        logger.info("OPC UA disconnected.")

    async def start_reading(self) -> None:
        if not self._client or not self._nodes:
            raise ProtocolError("OPC UA not connected or no nodes configured.")
        self._running = True
        await self._create_subscription()
        self._keep_alive_task = asyncio.create_task(self._keep_alive_loop())
        logger.info("OPC UA reading started with %d monitored nodes.", len(self._nodes))

    async def _create_subscription(self) -> None:
        handler = OPCUADataChangeHandler(self)
        self._subscription = await self._client.create_subscription(
            period=self.config.subscription_interval_ms,
            handler=handler,
        )
        for node_id, node in self._nodes.items():
            try:
                await self._subscription.subscribe_data_change(node)
                logger.debug("OPC UA subscribed to node: %s", node_id)
            except Exception as exc:
                logger.warning("OPC UA subscribe failed for node %s: %s", node_id, exc)

    async def _keep_alive_loop(self) -> None:
        while self._running:
            try:
                await asyncio.sleep(30)
                if self._client and self._connected:
                    # Read server time as keep-alive
                    server_node = self._client.get_node("i=2258")
                    await server_node.get_value()
            except asyncio.CancelledError:
                break
            except Exception as exc:
                logger.warning("OPC UA keep-alive failed: %s. Reconnecting.", exc)
                self._connected = False
                try:
                    await self.reconnect(max_attempts=5, delay=self.config.reconnect_delay_seconds)
                    await self._create_subscription()
                except ProtocolError as exc:
                    logger.critical("OPC UA permanent failure: %s", exc)
                    self._running = False

    async def _handle_datachange(self, node: Any, val: Any, data: Any) -> None:
        node_id = str(node.nodeid)
        quality = _opcua_quality(data)
        src_ts: Optional[datetime] = None
        try:
            if hasattr(data, "monitored_item") and hasattr(data.monitored_item, "Value"):
                src_ts_raw = data.monitored_item.Value.SourceTimestamp
                if src_ts_raw:
                    src_ts = src_ts_raw.replace(tzinfo=timezone.utc) if src_ts_raw.tzinfo is None else src_ts_raw
        except Exception:
            pass

        translated = _to_float(val)
        try:
            reading = DeviceReading(
                device_id=self.device_id,
                protocol=ProtocolType.OPC_UA,
                tag_name=node_id,
                raw_value=str(val),
                translated_value=translated,
                quality=quality,
                source_timestamp=src_ts,
                metadata={"nodeId": node_id},
            )
            await self._emit(reading)
        except Exception as exc:
            logger.warning("Failed to create DeviceReading from OPC UA datachange: %s", exc)

    async def write_value(self, tag_name: str, value: object) -> bool:
        if not self._client or not self._connected:
            logger.error("OPC UA not connected — cannot write to %s.", tag_name)
            return False
        node = self._nodes.get(tag_name)
        if node is None:
            # Try by direct node ID string
            try:
                node = self._client.get_node(tag_name)
            except Exception as exc:
                logger.error("OPC UA unknown node '%s': %s", tag_name, exc)
                return False
        try:
            from asyncua import ua
            dv = ua.DataValue(ua.Variant(value))
            await node.write_value(dv)
            logger.info("OPC UA wrote %s = %s", tag_name, value)
            return True
        except Exception as exc:
            logger.error("OPC UA write failed for %s: %s", tag_name, exc)
            return False

    async def read_tag(self, tag_name: str) -> Optional[Any]:
        """One-shot read of a single tag value."""
        if not self._client or not self._connected:
            return None
        node = self._nodes.get(tag_name) or self._client.get_node(tag_name)
        try:
            return await node.get_value()
        except Exception as exc:
            logger.error("OPC UA read failed for %s: %s", tag_name, exc)
            return None


def _opcua_quality(data: Any) -> DataQuality:
    try:
        if hasattr(data, "monitored_item") and hasattr(data.monitored_item, "Value"):
            status = data.monitored_item.Value.StatusCode
            if status is not None:
                code = int(status.value)
                if code & _OPCUA_BAD_STATUS_MASK:
                    return DataQuality.BAD
                if code & _OPCUA_UNCERTAIN_STATUS_MASK:
                    return DataQuality.UNCERTAIN
    except Exception:
        pass
    return DataQuality.GOOD


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
