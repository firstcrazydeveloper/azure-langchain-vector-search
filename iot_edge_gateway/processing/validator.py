from __future__ import annotations

import logging
import math
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional

from ..config import ValidationThreshold
from ..models import Alert, AlertSeverity, AlertType, DeviceReading, DataQuality

logger = logging.getLogger(__name__)


@dataclass
class ValidationResult:
    is_valid: bool = True
    alerts: list[Alert] = field(default_factory=list)
    reading: Optional[DeviceReading] = None


class DataValidator:
    """
    Validates device readings against configured thresholds.
    Generates alerts for threshold breaches, stale data, and data quality issues.
    """

    def __init__(self, thresholds: Optional[list[ValidationThreshold]] = None) -> None:
        self._thresholds: dict[str, ValidationThreshold] = {}
        self._last_good_reading: dict[str, datetime] = {}
        self._previous_values: dict[str, float] = {}

        for t in (thresholds or []):
            self._thresholds[t.tag_name] = t

    def add_threshold(self, threshold: ValidationThreshold) -> None:
        self._thresholds[threshold.tag_name] = threshold

    def validate(self, reading: DeviceReading) -> ValidationResult:
        alerts: list[Alert] = []
        is_valid = True

        # Quality validation
        if reading.quality == DataQuality.BAD:
            alerts.append(Alert(
                device_id=reading.device_id,
                tag_name=reading.tag_name,
                alert_type=AlertType.DATA_QUALITY,
                severity=AlertSeverity.WARNING,
                message=f"Bad data quality on device {reading.device_id}, tag {reading.tag_name}",
                metadata={"quality": reading.quality.value},
            ))
            is_valid = False

        threshold = self._thresholds.get(reading.tag_name)
        value = reading.translated_value

        if threshold is not None and value is not None:
            if not math.isfinite(value):
                alerts.append(Alert(
                    device_id=reading.device_id,
                    tag_name=reading.tag_name,
                    alert_type=AlertType.VALIDATION_FAILURE,
                    severity=AlertSeverity.CRITICAL,
                    message=f"Non-finite value for {reading.tag_name}: {value}",
                ))
                is_valid = False
            else:
                # Threshold breach checks
                alerts.extend(self._check_range(reading, threshold, value))
                alerts.extend(self._check_rate_of_change(reading, threshold, value))

        # Stale data check
        alerts.extend(self._check_stale(reading, threshold))

        if any(a.severity in (AlertSeverity.CRITICAL, AlertSeverity.EMERGENCY) for a in alerts):
            is_valid = False

        # Update tracking state
        if reading.quality == DataQuality.GOOD:
            self._last_good_reading[f"{reading.device_id}::{reading.tag_name}"] = datetime.now(timezone.utc)
        if value is not None and math.isfinite(value):
            self._previous_values[f"{reading.device_id}::{reading.tag_name}"] = value

        return ValidationResult(is_valid=is_valid, alerts=alerts, reading=reading)

    def validate_batch(self, readings: list[DeviceReading]) -> list[ValidationResult]:
        return [self.validate(r) for r in readings]

    def _check_range(
        self, reading: DeviceReading, threshold: ValidationThreshold, value: float
    ) -> list[Alert]:
        alerts: list[Alert] = []

        # Emergency/critical bounds first
        if threshold.critical_max is not None and value > threshold.critical_max:
            alerts.append(Alert(
                device_id=reading.device_id,
                tag_name=reading.tag_name,
                alert_type=AlertType.THRESHOLD_BREACH,
                severity=AlertSeverity.EMERGENCY,
                message=(
                    f"EMERGENCY: {reading.tag_name} = {value:.4f} exceeds critical max {threshold.critical_max:.4f}"
                ),
                value=value,
                threshold=threshold.critical_max,
            ))
        elif threshold.max_value is not None and value > threshold.max_value:
            alerts.append(Alert(
                device_id=reading.device_id,
                tag_name=reading.tag_name,
                alert_type=AlertType.THRESHOLD_BREACH,
                severity=AlertSeverity.CRITICAL,
                message=(
                    f"CRITICAL: {reading.tag_name} = {value:.4f} exceeds max {threshold.max_value:.4f}"
                ),
                value=value,
                threshold=threshold.max_value,
            ))

        if threshold.critical_min is not None and value < threshold.critical_min:
            alerts.append(Alert(
                device_id=reading.device_id,
                tag_name=reading.tag_name,
                alert_type=AlertType.THRESHOLD_BREACH,
                severity=AlertSeverity.EMERGENCY,
                message=(
                    f"EMERGENCY: {reading.tag_name} = {value:.4f} below critical min {threshold.critical_min:.4f}"
                ),
                value=value,
                threshold=threshold.critical_min,
            ))
        elif threshold.min_value is not None and value < threshold.min_value:
            alerts.append(Alert(
                device_id=reading.device_id,
                tag_name=reading.tag_name,
                alert_type=AlertType.THRESHOLD_BREACH,
                severity=AlertSeverity.CRITICAL,
                message=(
                    f"CRITICAL: {reading.tag_name} = {value:.4f} below min {threshold.min_value:.4f}"
                ),
                value=value,
                threshold=threshold.min_value,
            ))

        return alerts

    def _check_rate_of_change(
        self, reading: DeviceReading, threshold: ValidationThreshold, value: float
    ) -> list[Alert]:
        if threshold.max_rate_of_change is None:
            return []
        key = f"{reading.device_id}::{reading.tag_name}"
        prev = self._previous_values.get(key)
        if prev is None:
            return []
        rate = abs(value - prev)
        if rate > threshold.max_rate_of_change:
            return [Alert(
                device_id=reading.device_id,
                tag_name=reading.tag_name,
                alert_type=AlertType.RATE_OF_CHANGE,
                severity=AlertSeverity.WARNING,
                message=(
                    f"Rate of change {rate:.4f} exceeds limit {threshold.max_rate_of_change:.4f} "
                    f"for {reading.tag_name}"
                ),
                value=rate,
                threshold=threshold.max_rate_of_change,
            )]
        return []

    def _check_stale(
        self, reading: DeviceReading, threshold: Optional[ValidationThreshold]
    ) -> list[Alert]:
        stale_limit = threshold.stale_threshold_seconds if threshold else 300.0
        key = f"{reading.device_id}::{reading.tag_name}"
        last_good = self._last_good_reading.get(key)
        if last_good is None:
            return []
        elapsed = (datetime.now(timezone.utc) - last_good).total_seconds()
        if elapsed > stale_limit and reading.quality != DataQuality.GOOD:
            return [Alert(
                device_id=reading.device_id,
                tag_name=reading.tag_name,
                alert_type=AlertType.STALE_DATA,
                severity=AlertSeverity.WARNING,
                message=(
                    f"Stale data: last good reading for {reading.tag_name} was {elapsed:.0f}s ago "
                    f"(limit={stale_limit:.0f}s)"
                ),
                value=elapsed,
                threshold=stale_limit,
            )]
        return []
