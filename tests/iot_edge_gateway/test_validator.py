"""Tests for DataValidator — thresholds, rate-of-change, stale data."""
from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone

import pytest

from iot_edge_gateway.config import ValidationThreshold
from iot_edge_gateway.models import AlertSeverity, AlertType, DataQuality, DeviceReading, ProtocolType
from iot_edge_gateway.processing.validator import DataValidator


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


def _threshold(tag="temp", **kwargs):
    return ValidationThreshold(tag_name=tag, **kwargs)


class TestDataValidator:
    def test_valid_reading_no_alerts(self):
        v = DataValidator()
        result = v.validate(_reading(value=50.0))
        assert result.is_valid
        assert result.alerts == []

    def test_bad_quality_generates_alert(self):
        v = DataValidator()
        r = _reading(quality=DataQuality.BAD)
        result = v.validate(r)
        assert not result.is_valid
        assert any(a.alert_type == AlertType.DATA_QUALITY for a in result.alerts)

    def test_max_threshold_breach_critical(self):
        t = _threshold(max_value=100.0)
        v = DataValidator([t])
        result = v.validate(_reading(value=101.0))
        assert any(
            a.alert_type == AlertType.THRESHOLD_BREACH and a.severity == AlertSeverity.CRITICAL
            for a in result.alerts
        )

    def test_min_threshold_breach_critical(self):
        t = _threshold(min_value=50.0)
        v = DataValidator([t])
        result = v.validate(_reading(value=49.0))
        alerts = [a for a in result.alerts if a.alert_type == AlertType.THRESHOLD_BREACH]
        assert alerts
        assert alerts[0].severity == AlertSeverity.CRITICAL

    def test_critical_max_generates_emergency(self):
        t = _threshold(critical_max=200.0)
        v = DataValidator([t])
        result = v.validate(_reading(value=201.0))
        assert any(
            a.alert_type == AlertType.THRESHOLD_BREACH and a.severity == AlertSeverity.EMERGENCY
            for a in result.alerts
        )

    def test_critical_min_generates_emergency(self):
        t = _threshold(critical_min=10.0)
        v = DataValidator([t])
        result = v.validate(_reading(value=5.0))
        assert any(
            a.alert_type == AlertType.THRESHOLD_BREACH and a.severity == AlertSeverity.EMERGENCY
            for a in result.alerts
        )

    def test_within_range_no_alerts(self):
        t = _threshold(min_value=0.0, max_value=100.0)
        v = DataValidator([t])
        result = v.validate(_reading(value=50.0))
        assert result.alerts == []

    def test_rate_of_change_alert(self):
        t = _threshold(max_rate_of_change=5.0)
        v = DataValidator([t])
        v.validate(_reading(value=100.0))      # establish baseline
        result = v.validate(_reading(value=110.0))  # change=10 > 5
        roc_alerts = [a for a in result.alerts if a.alert_type == AlertType.RATE_OF_CHANGE]
        assert roc_alerts

    def test_rate_of_change_first_read_no_alert(self):
        t = _threshold(max_rate_of_change=5.0)
        v = DataValidator([t])
        result = v.validate(_reading(value=100.0))
        roc_alerts = [a for a in result.alerts if a.alert_type == AlertType.RATE_OF_CHANGE]
        assert not roc_alerts

    def test_stale_data_alert_after_bad_quality(self):
        t = _threshold(stale_threshold_seconds=1.0)
        v = DataValidator([t])
        # Establish last good reading
        good = _reading(quality=DataQuality.GOOD)
        v.validate(good)

        # Manually backdate last good reading
        key = "dev-1::temp"
        v._last_good_reading[key] = datetime.now(timezone.utc) - timedelta(seconds=10)

        bad = _reading(quality=DataQuality.BAD)
        result = v.validate(bad)
        stale = [a for a in result.alerts if a.alert_type == AlertType.STALE_DATA]
        assert stale

    def test_non_finite_value_alert(self):
        # Cannot set inf via pydantic, but we can test via internal path
        t = _threshold()
        v = DataValidator([t])
        r = _reading(value=50.0)
        # Force non-finite by manipulating after creation (bypasses validator)
        object.__setattr__(r, "translated_value", math.inf)
        result = v.validate(r)
        validation_alerts = [a for a in result.alerts if a.alert_type == AlertType.VALIDATION_FAILURE]
        assert validation_alerts

    def test_validate_batch_returns_all(self):
        v = DataValidator()
        readings = [_reading(tag=f"t{i}", value=float(i * 10)) for i in range(3)]
        results = v.validate_batch(readings)
        assert len(results) == 3

    def test_no_threshold_for_tag(self):
        v = DataValidator([_threshold(tag="other_tag")])
        result = v.validate(_reading(tag="temp", value=9999.0))
        assert result.alerts == []

    def test_value_exactly_at_max_no_alert(self):
        t = _threshold(max_value=100.0)
        v = DataValidator([t])
        result = v.validate(_reading(value=100.0))
        breach = [a for a in result.alerts if a.alert_type == AlertType.THRESHOLD_BREACH]
        assert not breach

    def test_alert_includes_value_and_threshold(self):
        t = _threshold(max_value=100.0)
        v = DataValidator([t])
        result = v.validate(_reading(value=150.0))
        a = next(x for x in result.alerts if x.alert_type == AlertType.THRESHOLD_BREACH)
        assert a.value == pytest.approx(150.0)
        assert a.threshold == pytest.approx(100.0)
