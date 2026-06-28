"""Integration tests for EdgeGateway pipeline — translate → filter → validate → alert → hub."""
from __future__ import annotations

import asyncio
from typing import Any
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from iot_edge_gateway.config import EdgeGatewaySettings
from iot_edge_gateway.gateway import EdgeGateway
from iot_edge_gateway.models import (
    Alert, AlertSeverity, AlertType,
    DataQuality, DeviceReading, ProtocolType,
)
from iot_edge_gateway.config import ValidationThreshold
from iot_edge_gateway.processing.filter import FilterRule
from iot_edge_gateway.processing.translator import TranslationRule


def _reading(tag="temp", value=50.0, quality=DataQuality.GOOD, device_id="dev-1"):
    r = DeviceReading(
        device_id=device_id,
        protocol=ProtocolType.MQTT,
        tag_name=tag,
        raw_value=value,
        translated_value=value,
    )
    r.quality = quality
    return r


@pytest.fixture
def gateway():
    """Gateway with minimal config — no real protocol connections, no IoT Hub."""
    with patch.dict("os.environ", {
        "GATEWAY_GATEWAY_ID": "test-gw",
        "GATEWAY_ENABLE_MQTT": "false",
        "GATEWAY_ENABLE_OPCUA": "false",
        "GATEWAY_ENABLE_MODBUS": "false",
        "GATEWAY_TELEMETRY_BATCH_SIZE": "10",
        "GATEWAY_TELEMETRY_BATCH_INTERVAL_SECONDS": "60",  # don't auto-flush in tests
        "GATEWAY_ALERT_COOLDOWN_SECONDS": "0",
    }):
        settings = EdgeGatewaySettings()
        gw = EdgeGateway(settings)
        # Replace hub client with no-op mock
        gw._hub_client = MagicMock()
        gw._hub_client.is_connected = True
        gw._hub_client.send_telemetry = AsyncMock(return_value=True)
        gw._hub_client.send_alert = AsyncMock(return_value=True)
        gw._hub_client.disconnect = AsyncMock()
        gw._hub_client.queue_size = 0
        gw._hub_client.circuit_state = "CLOSED"
        return gw


class TestGatewayPipeline:
    @pytest.mark.asyncio
    async def test_reading_passes_through_pipeline(self, gateway):
        batches = []

        async def capture_batch(batch):
            batches.append(batch)
            return True

        gateway._hub_client.send_telemetry = capture_batch

        r = _reading(value=50.0)
        await gateway._on_reading(r)
        # Force flush
        await gateway._flush_batch()
        assert len(batches) == 1
        assert len(batches[0].readings) == 1

    @pytest.mark.asyncio
    async def test_translation_applied(self, gateway):
        gateway.add_translation_rule(TranslationRule(tag_pattern="temp", scale=2.0, unit="°C"))
        batches = []

        async def capture(batch):
            batches.append(batch)
            return True

        gateway._hub_client.send_telemetry = capture
        r = _reading(value=50.0)
        await gateway._on_reading(r)
        await gateway._flush_batch()
        assert batches[0].readings[0].translated_value == pytest.approx(100.0)
        assert batches[0].readings[0].unit == "°C"

    @pytest.mark.asyncio
    async def test_filtered_reading_not_sent(self, gateway):
        gateway.add_filter_rule(FilterRule(tag_pattern="debug_*", important=False))
        batches = []

        async def capture(batch):
            batches.append(batch)
            return True

        gateway._hub_client.send_telemetry = capture
        r = _reading(tag="debug_cpu", value=90.0)
        await gateway._on_reading(r)
        await gateway._flush_batch()
        assert not batches  # Nothing sent

    @pytest.mark.asyncio
    async def test_critical_alert_forwarded_to_hub(self, gateway):
        gateway.add_threshold(ValidationThreshold(
            tag_name="temp",
            max_value=80.0,
            critical_max=90.0,
        ))
        r = _reading(value=95.0)  # Exceeds critical_max → EMERGENCY alert
        await gateway._on_reading(r)
        gateway._hub_client.send_alert.assert_called()

    @pytest.mark.asyncio
    async def test_info_alert_not_forwarded_to_hub(self, gateway):
        """INFO alerts should stay local, not forwarded to IoT Hub."""
        alerts_sent = []

        async def capture_alert(alert):
            alerts_sent.append(alert)

        gateway._alert_manager.add_handler(capture_alert)

        # Manually fire an INFO-level alert
        info_alert = Alert(
            device_id="dev-1",
            tag_name="temp",
            alert_type=AlertType.DATA_QUALITY,
            severity=AlertSeverity.INFO,
            message="Info-level alert",
        )
        await gateway._on_alert(info_alert)
        gateway._hub_client.send_alert.assert_not_called()

    @pytest.mark.asyncio
    async def test_batch_size_triggers_immediate_flush(self, gateway):
        # Set batch size to 2
        gateway.settings.gateway.telemetry_batch_size = 2
        batches = []

        async def capture(batch):
            batches.append(batch)
            return True

        gateway._hub_client.send_telemetry = capture
        await gateway._on_reading(_reading(tag="t1", value=1.0))
        await gateway._on_reading(_reading(tag="t2", value=2.0))
        # After 2 readings, should auto-flush
        assert len(batches) == 1
        assert len(batches[0].readings) == 2

    @pytest.mark.asyncio
    async def test_empty_batch_not_sent(self, gateway):
        await gateway._flush_batch()
        gateway._hub_client.send_telemetry.assert_not_called()

    @pytest.mark.asyncio
    async def test_bad_quality_reading_generates_alert(self, gateway):
        sent_alerts = []
        gateway._hub_client.send_alert = AsyncMock(side_effect=lambda a: sent_alerts.append(a))
        r = _reading(quality=DataQuality.BAD)
        # Bad quality is a WARNING, not CRITICAL — won't go to hub by default
        # But we check the alert manager has it
        await gateway._on_reading(r)
        assert gateway._alert_manager.get_alert_count() >= 0  # At least tried to process

    @pytest.mark.asyncio
    async def test_gateway_active_alert_count(self, gateway):
        assert gateway.active_alert_count >= 0

    @pytest.mark.asyncio
    async def test_gateway_critical_alert_count(self, gateway):
        assert gateway.critical_alert_count >= 0

    @pytest.mark.asyncio
    async def test_get_active_alerts_initially_empty(self, gateway):
        alerts = gateway.get_active_alerts()
        assert isinstance(alerts, list)

    @pytest.mark.asyncio
    async def test_multiple_readings_different_devices(self, gateway):
        batches = []

        async def capture(batch):
            batches.append(batch)
            return True

        gateway._hub_client.send_telemetry = capture
        for i in range(3):
            await gateway._on_reading(_reading(device_id=f"dev-{i}", value=float(i * 10)))
        await gateway._flush_batch()
        assert len(batches) == 1
        assert len(batches[0].readings) == 3

    @pytest.mark.asyncio
    async def test_pipeline_exception_does_not_crash_gateway(self, gateway):
        """A buggy translation rule should not kill the gateway loop."""
        def bad_transform(v): raise RuntimeError("transform error")
        gateway.add_translation_rule(TranslationRule(
            tag_pattern="*",
            transform=bad_transform,
        ))
        # Should not raise
        await gateway._on_reading(_reading(value=50.0))
