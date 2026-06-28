from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Optional

from ..models import DeviceReading, DataQuality

logger = logging.getLogger(__name__)


@dataclass
class FilterRule:
    """Defines filtering criteria for a tag or group of tags."""
    tag_pattern: str
    deadband_percent: float = 0.0       # Filter if change < deadband % of last value
    deadband_absolute: float = 0.0      # Filter if change < absolute deadband
    min_send_interval_seconds: float = 0.0  # Minimum time between sends for same tag
    always_send_on_quality_change: bool = True
    always_send_bad_quality: bool = True
    important: bool = True              # If False, tag is never forwarded to IoT Hub


_FILTER_MATCH_CACHE: dict[tuple[str, str], bool] = {}


def _matches_pattern(pattern: str, tag: str) -> bool:
    key = (pattern, tag)
    if key in _FILTER_MATCH_CACHE:
        return _FILTER_MATCH_CACHE[key]
    import fnmatch
    result = fnmatch.fnmatch(tag, pattern)
    _FILTER_MATCH_CACHE[key] = result
    return result


class _TagState:
    __slots__ = ("last_value", "last_sent_at", "last_quality")

    def __init__(self) -> None:
        self.last_value: Optional[float] = None
        self.last_sent_at: Optional[datetime] = None
        self.last_quality: Optional[DataQuality] = None


class DataFilter:
    """
    Filters device readings based on deadband, rate limiting, and tag importance.
    Readings that pass filtering are marked for forwarding; others set is_filtered=True.
    """

    def __init__(
        self,
        rules: Optional[list[FilterRule]] = None,
        default_deadband_percent: float = 0.0,
    ) -> None:
        self._rules: list[FilterRule] = rules or []
        self._default_deadband_percent = default_deadband_percent
        self._states: dict[str, _TagState] = {}  # keyed by "device_id::tag_name"

    def add_rule(self, rule: FilterRule) -> None:
        self._rules.append(rule)

    def filter(self, reading: DeviceReading) -> DeviceReading:
        rule = self._find_rule(reading.tag_name)

        if rule is not None and not rule.important:
            reading.is_filtered = True
            reading.filter_reason = "Tag not marked as important"
            return reading

        if reading.quality == DataQuality.BAD:
            if rule is None or rule.always_send_bad_quality:
                return reading  # Always forward BAD quality readings
            reading.is_filtered = True
            reading.filter_reason = "BAD quality suppressed by rule"
            return reading

        state = self._get_state(reading.device_id, reading.tag_name)

        # Check quality change — always send on transition
        if rule and rule.always_send_on_quality_change:
            if state.last_quality is not None and state.last_quality != reading.quality:
                self._update_state(state, reading)
                return reading

        # Check minimum send interval
        if rule and rule.min_send_interval_seconds > 0 and state.last_sent_at:
            elapsed = (datetime.now(timezone.utc) - state.last_sent_at).total_seconds()
            if elapsed < rule.min_send_interval_seconds:
                reading.is_filtered = True
                reading.filter_reason = f"Rate limited (elapsed={elapsed:.1f}s < min={rule.min_send_interval_seconds}s)"
                return reading

        # Check deadband
        if reading.translated_value is not None and state.last_value is not None:
            delta = abs(reading.translated_value - state.last_value)
            abs_deadband = (rule.deadband_absolute if rule else 0.0)
            pct_deadband = (rule.deadband_percent if rule else self._default_deadband_percent)

            should_filter = False
            filter_reason = ""

            if pct_deadband > 0 and state.last_value != 0.0:
                pct_change = (delta / abs(state.last_value)) * 100.0
                # within_abs: True when no abs deadband configured, or delta is within it
                within_abs = abs_deadband <= 0 or delta < abs_deadband
                if pct_change < pct_deadband and within_abs:
                    should_filter = True
                    filter_reason = f"Deadband ({pct_change:.3f}% < {pct_deadband}%)"

            # Absolute deadband check (also used when last_value==0 or no pct_deadband)
            if not should_filter and abs_deadband > 0 and delta < abs_deadband:
                should_filter = True
                filter_reason = f"Absolute deadband (delta={delta:.4f} < {abs_deadband})"

            if should_filter:
                reading.is_filtered = True
                reading.filter_reason = filter_reason
                return reading

        self._update_state(state, reading)
        return reading

    def filter_batch(self, readings: list[DeviceReading]) -> list[DeviceReading]:
        return [self.filter(r) for r in readings]

    def get_passthrough(self, readings: list[DeviceReading]) -> list[DeviceReading]:
        return [r for r in readings if not r.is_filtered]

    def _get_state(self, device_id: str, tag_name: str) -> _TagState:
        key = f"{device_id}::{tag_name}"
        if key not in self._states:
            self._states[key] = _TagState()
        return self._states[key]

    def _update_state(self, state: _TagState, reading: DeviceReading) -> None:
        state.last_value = reading.translated_value
        state.last_quality = reading.quality
        state.last_sent_at = datetime.now(timezone.utc)

    def _find_rule(self, tag_name: str) -> Optional[FilterRule]:
        for rule in self._rules:
            if _matches_pattern(rule.tag_pattern, tag_name):
                return rule
        return None
