from __future__ import annotations

import asyncio
import logging
from collections import defaultdict
from datetime import datetime, timezone
from typing import Callable, Coroutine, Optional

from ..models import Alert, AlertSeverity, AlertType

logger = logging.getLogger(__name__)

AlertHandler = Callable[[Alert], Coroutine]


class AlertManager:
    """
    Manages local alert lifecycle: deduplication, cooldown, escalation, and handler dispatch.
    Alerts of the same type+device+tag are suppressed during cooldown to avoid noise.
    """

    def __init__(self, cooldown_seconds: float = 60.0) -> None:
        self.cooldown_seconds = cooldown_seconds
        self._active_alerts: dict[str, Alert] = {}
        self._last_fired: dict[str, datetime] = {}
        self._handlers: list[AlertHandler] = []
        self._alert_counts: dict[str, int] = defaultdict(int)
        self._lock = asyncio.Lock()

    def add_handler(self, handler: AlertHandler) -> None:
        self._handlers.append(handler)

    async def process_alert(self, alert: Alert) -> bool:
        """Process an alert. Returns True if alert was fired (not suppressed by cooldown)."""
        key = self._alert_key(alert)
        async with self._lock:
            now = datetime.now(timezone.utc)
            last = self._last_fired.get(key)
            if last is not None:
                elapsed = (now - last).total_seconds()
                if elapsed < self.cooldown_seconds:
                    logger.debug(
                        "Alert suppressed (cooldown %.1fs remaining): %s",
                        self.cooldown_seconds - elapsed, key,
                    )
                    return False

            self._last_fired[key] = now
            self._active_alerts[key] = alert
            self._alert_counts[key] += 1
            alert.metadata["fire_count"] = self._alert_counts[key]

        logger.warning(
            "[ALERT][%s][%s] %s", alert.severity.value, alert.alert_type.value, alert.message
        )

        for handler in self._handlers:
            try:
                await handler(alert)
            except Exception as exc:
                logger.error("Alert handler failed: %s", exc, exc_info=True)

        return True

    async def process_alerts(self, alerts: list[Alert]) -> list[Alert]:
        """Process a list of alerts. Returns those that were actually fired."""
        fired: list[Alert] = []
        for alert in alerts:
            if await self.process_alert(alert):
                fired.append(alert)
        return fired

    async def acknowledge_alert(self, alert_id: str) -> bool:
        async with self._lock:
            for key, alert in self._active_alerts.items():
                if alert.alert_id == alert_id:
                    alert.acknowledge()
                    logger.info("Alert %s acknowledged.", alert_id)
                    return True
        return False

    async def resolve_alert(self, device_id: str, tag_name: str, alert_type: AlertType) -> None:
        key = self._alert_key_from_parts(device_id, tag_name, alert_type)
        async with self._lock:
            alert = self._active_alerts.pop(key, None)
            if alert:
                alert.resolve()
                logger.info("Alert resolved: %s", key)

    def get_active_alerts(
        self,
        min_severity: Optional[AlertSeverity] = None,
        device_id: Optional[str] = None,
    ) -> list[Alert]:
        severity_order = {
            AlertSeverity.INFO: 0,
            AlertSeverity.WARNING: 1,
            AlertSeverity.CRITICAL: 2,
            AlertSeverity.EMERGENCY: 3,
        }
        min_level = severity_order.get(min_severity, 0) if min_severity else 0
        result = []
        for alert in self._active_alerts.values():
            if severity_order[alert.severity] >= min_level:
                if device_id is None or alert.device_id == device_id:
                    result.append(alert)
        return result

    def get_alert_count(self) -> int:
        return len(self._active_alerts)

    def get_critical_alert_count(self) -> int:
        return sum(
            1 for a in self._active_alerts.values()
            if a.severity in (AlertSeverity.CRITICAL, AlertSeverity.EMERGENCY)
        )

    @staticmethod
    def _alert_key(alert: Alert) -> str:
        return f"{alert.device_id}::{alert.tag_name}::{alert.alert_type.value}"

    @staticmethod
    def _alert_key_from_parts(device_id: str, tag_name: str, alert_type: AlertType) -> str:
        return f"{device_id}::{tag_name}::{alert_type.value}"
