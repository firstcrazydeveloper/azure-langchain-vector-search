"""Tests for DataTranslator — translation rules and edge cases."""
from __future__ import annotations

import math

import pytest

from iot_edge_gateway.models import DataQuality, DeviceReading, ProtocolType
from iot_edge_gateway.processing.translator import DataTranslator, TranslationRule


def _reading(tag="sensor_1", raw=1000.0, quality=DataQuality.GOOD, translated=None):
    return DeviceReading(
        device_id="dev-1",
        protocol=ProtocolType.MODBUS_TCP,
        tag_name=tag,
        raw_value=raw,
        translated_value=translated,
    )


class TestTranslationRule:
    def test_scale_only(self):
        rule = TranslationRule(tag_pattern="x", scale=0.1)
        assert rule.apply(1000.0) == pytest.approx(100.0)

    def test_scale_and_offset(self):
        rule = TranslationRule(tag_pattern="x", scale=0.1, offset=-273.15)
        assert rule.apply(3731.5) == pytest.approx(100.0)

    def test_clamp_min(self):
        rule = TranslationRule(tag_pattern="x", scale=1.0, clamp_min=0.0)
        assert rule.apply(-5.0) == 0.0

    def test_clamp_max(self):
        rule = TranslationRule(tag_pattern="x", scale=1.0, clamp_max=10.0)
        assert rule.apply(100.0) == 10.0

    def test_custom_transform(self):
        rule = TranslationRule(tag_pattern="x", transform=lambda v: v ** 2)
        assert rule.apply(3.0) == pytest.approx(9.0)

    def test_scale_zero(self):
        rule = TranslationRule(tag_pattern="x", scale=0.0)
        assert rule.apply(9999.0) == 0.0

    def test_negative_scale(self):
        rule = TranslationRule(tag_pattern="x", scale=-1.0)
        assert rule.apply(5.0) == -5.0


class TestDataTranslator:
    def test_no_rules_passthrough(self):
        t = DataTranslator()
        r = _reading(raw=500.0)
        result = t.translate(r)
        assert result.translated_value is None or result.translated_value == 500.0

    def test_exact_tag_match(self):
        rule = TranslationRule(tag_pattern="sensor_1", scale=0.1, unit="bar")
        t = DataTranslator([rule])
        r = _reading(tag="sensor_1", raw=2000.0)
        result = t.translate(r)
        assert result.translated_value == pytest.approx(200.0)
        assert result.unit == "bar"

    def test_wildcard_match(self):
        rule = TranslationRule(tag_pattern="holding_*", scale=0.01, unit="°C")
        t = DataTranslator([rule])
        r = _reading(tag="holding_4001", raw=10000.0)
        result = t.translate(r)
        assert result.translated_value == pytest.approx(100.0)
        assert result.unit == "°C"

    def test_bad_quality_skipped(self):
        rule = TranslationRule(tag_pattern="*", scale=2.0)
        t = DataTranslator([rule])
        r = _reading(raw=100.0)
        r.quality = DataQuality.BAD
        result = t.translate(r)
        # translated_value should NOT be modified
        assert result.translated_value is None

    def test_non_finite_raw_sets_bad_quality(self):
        rule = TranslationRule(tag_pattern="*", scale=1.0)
        t = DataTranslator([rule])
        r = DeviceReading(
            device_id="d1",
            protocol=ProtocolType.MQTT,
            tag_name="x",
            raw_value=math.inf,   # non-finite raw; translated_value stays None
            translated_value=None,
        )
        result = t.translate(r)
        assert result.quality == DataQuality.BAD

    def test_first_rule_wins(self):
        rules = [
            TranslationRule(tag_pattern="tag", scale=2.0, unit="A"),
            TranslationRule(tag_pattern="tag", scale=3.0, unit="B"),
        ]
        t = DataTranslator(rules)
        r = _reading(tag="tag", raw=10.0)
        result = t.translate(r)
        assert result.unit == "A"
        assert result.translated_value == pytest.approx(20.0)

    def test_translate_batch(self):
        rule = TranslationRule(tag_pattern="*", scale=10.0)
        t = DataTranslator([rule])
        readings = [_reading(raw=1.0), _reading(raw=2.0)]
        results = t.translate_batch(readings)
        assert len(results) == 2
        assert results[0].translated_value == pytest.approx(10.0)
        assert results[1].translated_value == pytest.approx(20.0)

    def test_device_name_set_from_rule(self):
        rule = TranslationRule(tag_pattern="*", device_name="Furnace-1")
        t = DataTranslator([rule])
        r = _reading(raw=1.0)
        result = t.translate(r)
        assert result.device_name == "Furnace-1"

    def test_string_raw_value_translated(self):
        rule = TranslationRule(tag_pattern="*", scale=1.0)
        t = DataTranslator([rule])
        r = DeviceReading(
            device_id="d1", protocol=ProtocolType.MQTT, tag_name="x", raw_value="42"
        )
        result = t.translate(r)
        assert result.translated_value == pytest.approx(42.0)

    def test_non_numeric_string_no_crash(self):
        rule = TranslationRule(tag_pattern="*", scale=1.0)
        t = DataTranslator([rule])
        r = DeviceReading(
            device_id="d1", protocol=ProtocolType.MQTT, tag_name="x", raw_value="N/A"
        )
        result = t.translate(r)
        # Should not crash; translated_value remains None
        assert result.translated_value is None
