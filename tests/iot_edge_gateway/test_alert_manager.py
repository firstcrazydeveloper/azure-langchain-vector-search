"""Tests for AlertManager — deduplication, cooldown, handlers."""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from typing import List

import pytest
import pytest_asyncio

from iot_edge_gateway.models import Alert, AlertSeverity, AlertType
from iot_edge_gateway.processing.alert_manager import AlertManager


def _alert(device_id="dev-1", tag="temp", severity=AlertSeverity.CRITICAL):
    return Alert(
        device_id=device_id,
        tag_name=tag,
        alert_type=AlertType.THRESHOLD_BREACH,
        severity=severity,
        message="Test alert",
    )


@pytest.fixture
def mgr():
    return AlertManager(cooldown_seconds=60.0)


class TestAlertManager:
    @pytest.mark.asyncio
    async def test_first_alert_fires(self, mgr):
        fired = []
        async def handler(a): fired.append(a)
        mgr.add_handler(handler)
        result = await mgr.process_alert(_alert())
        assert result
        assert len(fired) == 1

    @pytest.mark.asyncio
    async def test_duplicate_suppressed_by_cooldown(self, mgr):
        fired = []
        async def handler(a): fired.append(a)
        mgr.add_handler(handler)
        await mgr.process_alert(_alert())
        result = await mgr.process_alert(_alert())
        assert not result
        assert len(fired) == 1

    @pytest.mark.asyncio
    async def test_different_devices_not_suppressed(self, mgr):
        fired = []
        async def handler(a): fired.append(a)
        mgr.add_handler(handler)
        await mgr.process_alert(_alert(device_id="dev-1"))
        result = await mgr.process_alert(_alert(device_id="dev-2"))
        assert result
        assert len(fired) == 2

    @pytest.mark.asyncio
    async def test_different_tags_not_suppressed(self, mgr):
        fired = []
        async def handler(a): fired.append(a)
        mgr.add_handler(handler)
        await mgr.process_alert(_alert(tag="temp"))
        result = await mgr.process_alert(_alert(tag="pressure"))
        assert result
        assert len(fired) == 2

    @pytest.mark.asyncio
    async def test_zero_cooldown_always_fires(self):
        mgr = AlertManager(cooldown_seconds=0.0)
        fired = []
        async def handler(a): fired.append(a)
        mgr.add_handler(handler)
        await mgr.process_alert(_alert())
        await mgr.process_alert(_alert())
        assert len(fired) == 2

    @pytest.mark.asyncio
    async def test_process_alerts_batch(self, mgr):
        alerts = [_alert(tag=f"tag{i}") for i in range(5)]
        fired = await mgr.process_alerts(alerts)
        assert len(fired) == 5

    @pytest.mark.asyncio
    async def test_acknowledge_alert(self, mgr):
        a = _alert()
        await mgr.process_alert(a)
        result = await mgr.acknowledge_alert(a.alert_id)
        assert result
        assert a.acknowledged

    @pytest.mark.asyncio
    async def test_acknowledge_unknown_returns_false(self, mgr):
        result = await mgr.acknowledge_alert("non-existent-id")
        assert not result

    @pytest.mark.asyncio
    async def test_resolve_alert(self, mgr):
        a = _alert(tag="temp")
        await mgr.process_alert(a)
        await mgr.resolve_alert("dev-1", "temp", AlertType.THRESHOLD_BREACH)
        assert mgr.get_alert_count() == 0

    @pytest.mark.asyncio
    async def test_get_active_alerts_filter_by_severity(self, mgr):
        await mgr.process_alert(_alert(tag="t1", severity=AlertSeverity.INFO))
        await mgr.process_alert(_alert(tag="t2", severity=AlertSeverity.CRITICAL))
        critical = mgr.get_active_alerts(min_severity=AlertSeverity.CRITICAL)
        assert len(critical) == 1

    @pytest.mark.asyncio
    async def test_get_active_alerts_filter_by_device(self, mgr):
        await mgr.process_alert(_alert(device_id="dev-1", tag="t1"))
        await mgr.process_alert(_alert(device_id="dev-2", tag="t2"))
        dev1_alerts = mgr.get_active_alerts(device_id="dev-1")
        assert len(dev1_alerts) == 1
        assert dev1_alerts[0].device_id == "dev-1"

    @pytest.mark.asyncio
    async def test_handler_exception_does_not_crash(self, mgr):
        async def bad_handler(a): raise RuntimeError("handler error")
        mgr.add_handler(bad_handler)
        result = await mgr.process_alert(_alert())
        assert result  # Alert still fired even though handler crashed

    @pytest.mark.asyncio
    async def test_fire_count_metadata(self, mgr):
        mgr2 = AlertManager(cooldown_seconds=0.0)
        a1 = _alert()
        a2 = _alert()
        await mgr2.process_alert(a1)
        await mgr2.process_alert(a2)
        assert a2.metadata.get("fire_count") == 2

    def test_get_critical_alert_count(self, mgr):
        # No alerts yet
        assert mgr.get_critical_alert_count() == 0
