from __future__ import annotations

import logging
import math
from dataclasses import dataclass, field
from typing import Any, Callable, Optional

from ..models import DeviceReading, DataQuality

logger = logging.getLogger(__name__)


@dataclass
class TranslationRule:
    """Defines how to translate raw register/tag values to engineering units."""
    tag_pattern: str              # Exact tag name or glob pattern (e.g. "holding_*")
    scale: float = 1.0            # Multiplier applied to raw value
    offset: float = 0.0          # Added after scaling
    unit: str = ""                # Engineering unit label (°C, bar, rpm, ...)
    device_name: str = ""
    clamp_min: Optional[float] = None
    clamp_max: Optional[float] = None
    transform: Optional[Callable[[float], float]] = field(default=None, repr=False)

    def apply(self, raw: float) -> float:
        value = raw * self.scale + self.offset
        if self.transform:
            value = self.transform(value)
        if self.clamp_min is not None:
            value = max(value, self.clamp_min)
        if self.clamp_max is not None:
            value = min(value, self.clamp_max)
        return value


_WILDCARD_MATCH_CACHE: dict[tuple[str, str], bool] = {}


def _matches_pattern(pattern: str, tag: str) -> bool:
    key = (pattern, tag)
    if key in _WILDCARD_MATCH_CACHE:
        return _WILDCARD_MATCH_CACHE[key]
    import fnmatch
    result = fnmatch.fnmatch(tag, pattern)
    _WILDCARD_MATCH_CACHE[key] = result
    return result


class DataTranslator:
    """
    Translates raw DeviceReading values into engineering units.
    Rules are applied in registration order; first match wins.
    """

    def __init__(self, rules: Optional[list[TranslationRule]] = None) -> None:
        self._rules: list[TranslationRule] = rules or []

    def add_rule(self, rule: TranslationRule) -> None:
        self._rules.append(rule)

    def translate(self, reading: DeviceReading) -> DeviceReading:
        if reading.quality == DataQuality.BAD:
            logger.debug("Skipping translation for BAD quality reading: %s", reading.tag_name)
            return reading

        rule = self._find_rule(reading.tag_name)
        if rule is None:
            return reading

        raw_float = reading.translated_value
        if raw_float is None:
            raw_float = _safe_float(reading.raw_value)
        if raw_float is None:
            logger.debug("Cannot translate non-numeric raw_value for tag %s.", reading.tag_name)
            return reading

        if not math.isfinite(raw_float):
            logger.warning("Non-finite raw_value for tag %s: %s. Setting quality=BAD.", reading.tag_name, raw_float)
            reading.quality = DataQuality.BAD
            return reading

        try:
            translated = rule.apply(raw_float)
        except Exception as exc:
            logger.error("Translation rule failed for tag %s: %s", reading.tag_name, exc)
            reading.quality = DataQuality.UNCERTAIN
            return reading

        reading.translated_value = translated
        reading.unit = rule.unit
        if rule.device_name:
            reading.device_name = rule.device_name
        reading.metadata["translation_rule"] = rule.tag_pattern
        return reading

    def translate_batch(self, readings: list[DeviceReading]) -> list[DeviceReading]:
        return [self.translate(r) for r in readings]

    def _find_rule(self, tag_name: str) -> Optional[TranslationRule]:
        for rule in self._rules:
            if _matches_pattern(rule.tag_pattern, tag_name):
                return rule
        return None


def _safe_float(value: Any) -> Optional[float]:
    if value is None:
        return None
    if isinstance(value, bool):
        return float(value)
    try:
        return float(value)
    except (TypeError, ValueError):
        return None
