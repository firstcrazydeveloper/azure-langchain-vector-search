"""Tests for protocol adapters — connection, parsing, write, edge cases."""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from typing import Any, Optional
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from iot_edge_gateway.config import MQTTConfig, ModbusConfig, OPCUAConfig
from iot_edge_gateway.models import DataQuality, DeviceReading, ProtocolType
from iot_edge_gateway.protocols.mqtt_protocol import (
    MQTTProtocol,
    _coerce_scalar,
    _parse_iso_timestamp,
    _to_float,
)
from iot_edge_gateway.protocols.modbus_protocol import ModbusTCPProtocol, _chunk_addresses
from iot_edge_gateway.protocols.base_protocol import ProtocolError


# ─────────────────── MQTT Protocol ───────────────────

class TestMQTTProtocolParsing:
    """Unit tests for the pure parsing logic — no network needed."""

    def _proto(self):
        return MQTTProtocol(MQTTConfig(), device_id="test-gw")

    def test_parse_json_message(self):
        proto = self._proto()
        msg = {
            "topic": "devices/sensor1/temperature",
            "payload": '{"value": 72.5, "quality": "GOOD"}',
            "qos": 1,
        }
        reading = proto._parse_message(msg)
        assert reading is not None
        assert reading.device_id == "sensor1"
        assert reading.translated_value == pytest.approx(72.5)
        assert reading.quality == DataQuality.GOOD

    def test_parse_raw_numeric_payload(self):
        proto = self._proto()
        msg = {"topic": "devices/pump/pressure", "payload": "3.14", "qos": 0}
        reading = proto._parse_message(msg)
        assert reading is not None
        assert reading.translated_value == pytest.approx(3.14)

    def test_parse_empty_payload_returns_none(self):
        proto = self._proto()
        msg = {"topic": "devices/s/t", "payload": "", "qos": 0}
        result = proto._parse_message(msg)
        assert result is None

    def test_parse_whitespace_payload_returns_none(self):
        proto = self._proto()
        msg = {"topic": "devices/s/t", "payload": "   ", "qos": 0}
        result = proto._parse_message(msg)
        assert result is None

    def test_parse_boolean_payload(self):
        proto = self._proto()
        msg = {"topic": "devices/relay/coil1", "payload": "true", "qos": 1}
        reading = proto._parse_message(msg)
        assert reading is not None
        assert reading.translated_value == 1.0

    def test_parse_unknown_quality_sets_uncertain(self):
        proto = self._proto()
        msg = {
            "topic": "devices/s/t",
            "payload": '{"value": 10.0, "quality": "INVALID_QUALITY"}',
            "qos": 0,
        }
        reading = proto._parse_message(msg)
        assert reading is not None
        assert reading.quality == DataQuality.UNCERTAIN

    def test_parse_json_with_custom_tag_name(self):
        proto = self._proto()
        msg = {
            "topic": "devices/sensor1/data",
            "payload": '{"value": 55.0, "tag": "temperature", "quality": "GOOD"}',
            "qos": 0,
        }
        reading = proto._parse_message(msg)
        assert reading is not None
        assert reading.tag_name == "temperature"

    def test_parse_topic_device_extraction(self):
        proto = self._proto()
        msg = {"topic": "devices/boiler-3/flow", "payload": "12.34", "qos": 0}
        reading = proto._parse_message(msg)
        assert reading is not None
        assert reading.device_id == "boiler-3"
        assert reading.tag_name == "flow"

    def test_parse_topic_single_segment(self):
        proto = self._proto()
        msg = {"topic": "single", "payload": "1.0", "qos": 0}
        reading = proto._parse_message(msg)
        assert reading is not None
        assert reading.device_id == "test-gw"

    def test_parse_invalid_json_fallback(self):
        proto = self._proto()
        msg = {"topic": "devices/s/t", "payload": "{not_json}", "qos": 0}
        reading = proto._parse_message(msg)
        assert reading is not None

    def test_metadata_includes_topic(self):
        proto = self._proto()
        msg = {"topic": "devices/dev/tag", "payload": "1.0", "qos": 2}
        reading = proto._parse_message(msg)
        assert reading is not None
        assert reading.metadata["topic"] == "devices/dev/tag"
        assert reading.metadata["qos"] == 2


class TestMQTTHelpers:
    def test_to_float_numeric(self):
        assert _to_float(42) == pytest.approx(42.0)
        assert _to_float(3.14) == pytest.approx(3.14)

    def test_to_float_bool(self):
        assert _to_float(True) == 1.0
        assert _to_float(False) == 0.0

    def test_to_float_none(self):
        assert _to_float(None) is None

    def test_to_float_inf_returns_none(self):
        import math
        assert _to_float(math.inf) is None

    def test_to_float_nan_returns_none(self):
        import math
        assert _to_float(math.nan) is None

    def test_to_float_string_numeric(self):
        assert _to_float("42.5") == pytest.approx(42.5)

    def test_to_float_non_numeric_string(self):
        assert _to_float("hello") is None

    def test_coerce_scalar_int(self):
        assert _coerce_scalar("42") == 42

    def test_coerce_scalar_float(self):
        assert _coerce_scalar("3.14") == pytest.approx(3.14)

    def test_coerce_scalar_bool_true(self):
        assert _coerce_scalar("true") is True
        assert _coerce_scalar("on") is True

    def test_coerce_scalar_bool_false(self):
        assert _coerce_scalar("false") is False
        assert _coerce_scalar("off") is False

    def test_coerce_scalar_string(self):
        result = _coerce_scalar("hello")
        assert result == "hello"

    def test_parse_iso_timestamp_valid(self):
        ts = _parse_iso_timestamp("2024-01-15T10:30:00Z")
        assert ts is not None
        assert ts.tzinfo is not None

    def test_parse_iso_timestamp_none(self):
        assert _parse_iso_timestamp(None) is None

    def test_parse_iso_timestamp_invalid(self):
        assert _parse_iso_timestamp("not-a-date") is None

    def test_parse_iso_timestamp_naive_gets_utc(self):
        ts = _parse_iso_timestamp("2024-01-15T10:30:00")
        assert ts is not None
        assert ts.tzinfo is not None


class TestMQTTConnect:
    @pytest.mark.asyncio
    async def test_connect_raises_on_missing_paho(self):
        proto = MQTTProtocol(MQTTConfig(broker_host="localhost"), device_id="gw")
        with patch.dict("sys.modules", {"paho": None, "paho.mqtt": None, "paho.mqtt.client": None}):
            with pytest.raises(ProtocolError, match="paho-mqtt"):
                await proto.connect()


# ─────────────────── Modbus TCP Protocol ───────────────────

class TestChunkAddresses:
    def test_empty(self):
        assert _chunk_addresses([], 125) == []

    def test_single(self):
        assert _chunk_addresses([100], 125) == [[100]]

    def test_consecutive(self):
        result = _chunk_addresses([1, 2, 3, 4, 5], 125)
        assert result == [[1, 2, 3, 4, 5]]

    def test_max_count_splits(self):
        addrs = list(range(1, 130))  # 129 consecutive
        result = _chunk_addresses(addrs, 125)
        assert len(result) == 2
        assert len(result[0]) == 125
        assert result[0][0] == 1

    def test_non_consecutive_splits(self):
        result = _chunk_addresses([1, 2, 10, 11], 125)
        # Gap between 2 and 10 → two chunks
        assert len(result) == 2

    def test_single_at_boundary(self):
        result = _chunk_addresses([0, 125], 125)
        # 0 and 125 are 126 apart → gap splits them
        assert len(result) == 2


class TestModbusTCPProtocol:
    @pytest.mark.asyncio
    async def test_connect_raises_on_missing_pymodbus(self):
        proto = ModbusTCPProtocol(ModbusConfig(), device_id="plc-1")
        with patch.dict("sys.modules", {"pymodbus": None, "pymodbus.client": None}):
            with pytest.raises(ProtocolError, match="pymodbus"):
                await proto.connect()

    @pytest.mark.asyncio
    async def test_write_without_connect_returns_false(self):
        proto = ModbusTCPProtocol(ModbusConfig(), device_id="plc-1")
        result = await proto.write_value("holding_100", 200)
        assert result is False

    @pytest.mark.asyncio
    async def test_write_invalid_tag_format_returns_false(self):
        proto = ModbusTCPProtocol(ModbusConfig(), device_id="plc-1")
        proto._connected = True
        proto._client = MagicMock()
        result = await proto.write_value("bad_format_tag_here", 1)
        assert result is False

    @pytest.mark.asyncio
    async def test_read_tag_without_connect_returns_none(self):
        proto = ModbusTCPProtocol(ModbusConfig(), device_id="plc-1")
        result = await proto.read_tag("holding_100")
        assert result is None

    @pytest.mark.asyncio
    async def test_emit_reading_bad_value_no_crash(self):
        proto = ModbusTCPProtocol(ModbusConfig(), device_id="plc-1")
        collected = []
        async def cb(r): collected.append(r)
        proto.set_data_callback(cb)
        # Simulate emit with None raw (bad quality)
        await proto._emit_reading("coil_0", None, None, DataQuality.BAD, datetime.now(timezone.utc))
        assert len(collected) == 1
        assert collected[0].quality == DataQuality.BAD

    @pytest.mark.asyncio
    async def test_write_holding_register_out_of_range_returns_false(self):
        proto = ModbusTCPProtocol(ModbusConfig(), device_id="plc-1")
        proto._connected = True
        proto._client = MagicMock()
        result = await proto.write_value("holding_100", 70000)
        assert result is False
