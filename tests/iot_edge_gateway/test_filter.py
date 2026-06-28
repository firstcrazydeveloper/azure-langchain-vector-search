"""Tests for DataFilter — deadband, rate limiting, importance, edge cases."""
from __future__ import annotations

import time
from datetime import datetime, timedelta, timezone

import pytest

from iot_edge_gateway.models import DataQuality, DeviceReading, ProtocolType
from iot_edge_gateway.processing.filter import DataFilter, FilterRule


def _reading(tag="temp", value=100.0, quality=DataQuality.GOOD, device_id="dev-1"):
    r = DeviceReading(
        device_id=device_id,
        protocol=ProtocolType.MODBUS_TCP,
        tag_name=tag,
        raw_value=value,
        translated_value=value,
    )
    r.quality = quality
    return r


class TestDataFilter:
    def test_no_rules_passes_all(self):
        f = DataFilter()
        r = _reading()
        result = f.filter(r)
        assert not result.is_filtered

    def test_important_false_filters(self):
        rule = FilterRule(tag_pattern="debug_*", important=False)
        f = DataFilter([rule])
        r = _reading(tag="debug_cpu")
        result = f.filter(r)
        assert result.is_filtered
        assert "not marked as important" in result.filter_reason

    def test_bad_quality_always_passes_by_default(self):
        rule = FilterRule(tag_pattern="*")
        f = DataFilter([rule])
        r = _reading(quality=DataQuality.BAD)
        result = f.filter(r)
        assert not result.is_filtered

    def test_bad_quality_suppressed_by_rule(self):
        rule = FilterRule(tag_pattern="*", always_send_bad_quality=False)
        f = DataFilter([rule])
        r = _reading(quality=DataQuality.BAD)
        result = f.filter(r)
        assert result.is_filtered

    def test_first_reading_always_passes(self):
        rule = FilterRule(tag_pattern="*", deadband_percent=5.0)
        f = DataFilter([rule])
        r = _reading(value=100.0)
        result = f.filter(r)
        assert not result.is_filtered

    def test_deadband_percent_filters_small_change(self):
        rule = FilterRule(tag_pattern="*", deadband_percent=5.0)
        f = DataFilter([rule])
        f.filter(_reading(value=100.0))  # establish baseline
        r = _reading(value=102.0)         # 2% change < 5%
        result = f.filter(r)
        assert result.is_filtered
        assert "Deadband" in result.filter_reason

    def test_deadband_percent_passes_large_change(self):
        rule = FilterRule(tag_pattern="*", deadband_percent=5.0)
        f = DataFilter([rule])
        f.filter(_reading(value=100.0))
        r = _reading(value=110.0)  # 10% change > 5%
        result = f.filter(r)
        assert not result.is_filtered

    def test_absolute_deadband_filters(self):
        rule = FilterRule(tag_pattern="*", deadband_absolute=1.0)
        f = DataFilter([rule])
        f.filter(_reading(value=50.0))
        r = _reading(value=50.5)   # delta=0.5 < 1.0
        result = f.filter(r)
        assert result.is_filtered

    def test_quality_change_always_sends(self):
        rule = FilterRule(tag_pattern="*", deadband_percent=99.0, always_send_on_quality_change=True)
        f = DataFilter([rule])
        r1 = _reading(value=100.0, quality=DataQuality.GOOD)
        f.filter(r1)
        r2 = _reading(value=100.0, quality=DataQuality.UNCERTAIN)
        result = f.filter(r2)
        assert not result.is_filtered

    def test_rate_limit_suppresses_fast_sends(self):
        rule = FilterRule(tag_pattern="*", min_send_interval_seconds=10.0)
        f = DataFilter([rule])
        f.filter(_reading(value=100.0))
        r = _reading(value=200.0)
        result = f.filter(r)
        assert result.is_filtered
        assert "Rate limited" in result.filter_reason

    def test_different_devices_independent_state(self):
        rule = FilterRule(tag_pattern="*", deadband_percent=5.0)
        f = DataFilter([rule])
        f.filter(_reading(device_id="dev-A", value=100.0))
        f.filter(_reading(device_id="dev-B", value=100.0))
        r_a = _reading(device_id="dev-A", value=102.0)  # 2% < 5%
        r_b = _reading(device_id="dev-B", value=200.0)  # 100% > 5%
        assert f.filter(r_a).is_filtered
        assert not f.filter(r_b).is_filtered

    def test_filter_batch_returns_all(self):
        f = DataFilter()
        readings = [_reading(tag=f"t{i}", value=float(i)) for i in range(5)]
        results = f.filter_batch(readings)
        assert len(results) == 5

    def test_get_passthrough_excludes_filtered(self):
        rule = FilterRule(tag_pattern="debug_*", important=False)
        f = DataFilter([rule])
        readings = [
            _reading(tag="temp", value=1.0),
            _reading(tag="debug_cpu", value=2.0),
        ]
        filtered = f.filter_batch(readings)
        passed = f.get_passthrough(filtered)
        assert len(passed) == 1
        assert passed[0].tag_name == "temp"

    def test_zero_last_value_deadband_skips_percent(self):
        rule = FilterRule(tag_pattern="*", deadband_percent=5.0, deadband_absolute=1.0)
        f = DataFilter([rule])
        f.filter(_reading(value=0.0))
        r = _reading(value=0.5)  # delta=0.5 < abs_deadband=1.0 → should filter
        result = f.filter(r)
        assert result.is_filtered
