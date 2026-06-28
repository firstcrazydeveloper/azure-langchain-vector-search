"""Tests for DeviceReading, Alert, C2DCommand models — edge case validation."""
from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone

import pytest

from iot_edge_gateway.models import (
    Alert, AlertSeverity, AlertType,
    C2DCommand, CommandStatus, CommandType,
    DataQuality, DeviceReading, ProtocolType, TelemetryBatch,
)


# ─────────────────────────── DeviceReading ───────────────────────────

class TestDeviceReading:
    def _make(self, **kwargs):
        defaults = {
            "device_id": "dev-1",
            "protocol": ProtocolType.MQTT,
            "tag_name": "temperature",
            "raw_value": 42.0,
        }
        return DeviceReading(**{**defaults, **kwargs})

    def test_basic_creation(self):
        r = self._make()
        assert r.device_id == "dev-1"
        assert r.quality == DataQuality.GOOD
        assert r.timestamp.tzinfo is not None

    def test_empty_device_id_raises(self):
        with pytest.raises(ValueError, match="device_id"):
            self._make(device_id="")

    def test_whitespace_device_id_raises(self):
        with pytest.raises(ValueError, match="device_id"):
            self._make(device_id="   ")

    def test_empty_tag_name_raises(self):
        with pytest.raises(ValueError, match="tag_name"):
            self._make(tag_name="")

    def test_nan_translated_value_raises(self):
        with pytest.raises(ValueError, match="finite"):
            self._make(translated_value=math.nan)

    def test_inf_translated_value_raises(self):
        with pytest.raises(ValueError, match="finite"):
            self._make(translated_value=math.inf)

    def test_future_source_timestamp_sets_uncertain(self):
        future = datetime.now(timezone.utc) + timedelta(hours=1)
        r = self._make(source_timestamp=future)
        assert r.quality == DataQuality.UNCERTAIN
        assert "timestamp_warning" in r.metadata

    def test_past_source_timestamp_ok(self):
        past = datetime.now(timezone.utc) - timedelta(hours=1)
        r = self._make(source_timestamp=past)
        assert r.quality == DataQuality.GOOD

    def test_to_telemetry_dict_keys(self):
        r = self._make(translated_value=100.0, unit="°C")
        d = r.to_telemetry_dict()
        assert set(d.keys()) >= {"readingId", "deviceId", "protocol", "tagName", "value", "unit", "quality", "timestamp"}
        assert d["value"] == 100.0

    def test_to_telemetry_dict_uses_raw_when_no_translated(self):
        r = self._make(raw_value=55, translated_value=None)
        d = r.to_telemetry_dict()
        assert d["value"] == 55

    def test_device_id_trimmed(self):
        r = self._make(device_id="  dev-2  ")
        assert r.device_id == "dev-2"

    def test_tag_name_trimmed(self):
        r = self._make(tag_name="  temp  ")
        assert r.tag_name == "temp"

    def test_none_translated_value_ok(self):
        r = self._make(translated_value=None)
        assert r.translated_value is None

    def test_zero_translated_value_ok(self):
        r = self._make(translated_value=0.0)
        assert r.translated_value == 0.0


class TestTelemetryBatch:
    def _reading(self, device_id="d1", tag="t"):
        return DeviceReading(device_id=device_id, protocol=ProtocolType.MQTT, tag_name=tag, raw_value=1.0)

    def test_empty_readings_raises(self):
        with pytest.raises(ValueError, match="at least one"):
            TelemetryBatch(gateway_id="gw-1", readings=[])

    def test_batch_message_structure(self):
        b = TelemetryBatch(gateway_id="gw-1", readings=[self._reading()])
        msg = b.to_iothub_message()
        assert msg["gatewayId"] == "gw-1"
        assert msg["count"] == 1
        assert len(msg["readings"]) == 1

    def test_batch_id_unique(self):
        b1 = TelemetryBatch(gateway_id="gw-1", readings=[self._reading()])
        b2 = TelemetryBatch(gateway_id="gw-1", readings=[self._reading()])
        assert b1.batch_id != b2.batch_id


# ─────────────────────────── Alert ───────────────────────────

class TestAlert:
    def _make(self, **kwargs):
        defaults = {
            "device_id": "dev-1",
            "alert_type": AlertType.THRESHOLD_BREACH,
            "severity": AlertSeverity.CRITICAL,
            "message": "Value exceeded threshold",
        }
        return Alert(**{**defaults, **kwargs})

    def test_basic_alert(self):
        a = self._make()
        assert not a.acknowledged
        assert not a.resolved
        assert a.resolved_at is None

    def test_empty_message_raises(self):
        with pytest.raises(ValueError, match="message"):
            self._make(message="")

    def test_whitespace_message_raises(self):
        with pytest.raises(ValueError, match="message"):
            self._make(message="   ")

    def test_acknowledge(self):
        a = self._make()
        a.acknowledge()
        assert a.acknowledged

    def test_resolve(self):
        a = self._make()
        a.resolve()
        assert a.resolved
        assert a.resolved_at is not None

    def test_to_dict_keys(self):
        a = self._make()
        d = a.to_dict()
        assert set(d.keys()) >= {"alertId", "deviceId", "alertType", "severity", "message", "timestamp"}

    def test_alert_id_unique(self):
        a1 = self._make()
        a2 = self._make()
        assert a1.alert_id != a2.alert_id


# ─────────────────────────── C2DCommand ───────────────────────────

class TestC2DCommand:
    def _make(self, **kwargs):
        defaults = {
            "target_device_id": "dev-1",
            "command_type": CommandType.WRITE_TAG,
            "parameters": {"tagName": "holding_100", "value": 200},
        }
        return C2DCommand(**{**defaults, **kwargs})

    def test_basic_command(self):
        c = self._make()
        assert c.status == CommandStatus.PENDING

    def test_empty_target_raises(self):
        with pytest.raises(ValueError, match="target_device_id"):
            self._make(target_device_id="")

    def test_timeout_bounds(self):
        with pytest.raises(Exception):
            self._make(timeout_seconds=0)
        with pytest.raises(Exception):
            self._make(timeout_seconds=9999)

    def test_mark_executing(self):
        c = self._make()
        c.mark_executing()
        assert c.status == CommandStatus.EXECUTING
        assert c.executed_at is not None

    def test_mark_success(self):
        c = self._make()
        c.mark_success({"result": "ok"})
        assert c.status == CommandStatus.SUCCESS
        assert c.result == {"result": "ok"}

    def test_mark_failed(self):
        c = self._make()
        c.mark_failed("Device offline")
        assert c.status == CommandStatus.FAILED
        assert "Device offline" in c.error_message

    def test_mark_timeout(self):
        c = self._make(timeout_seconds=1)
        c.mark_timeout()
        assert c.status == CommandStatus.TIMEOUT

    def test_mark_rejected(self):
        c = self._make()
        c.mark_rejected("Unauthorized")
        assert c.status == CommandStatus.REJECTED

    def test_is_expired_when_not(self):
        c = self._make(timeout_seconds=3600)
        assert not c.is_expired()

    def test_is_expired_when_yes(self):
        from datetime import timedelta
        c = self._make(timeout_seconds=1)
        c.issued_at = datetime.now(timezone.utc) - timedelta(seconds=10)
        assert c.is_expired()

    def test_to_response_dict(self):
        c = self._make()
        d = c.to_response_dict()
        assert d["status"] == "PENDING"
        assert "commandId" in d
