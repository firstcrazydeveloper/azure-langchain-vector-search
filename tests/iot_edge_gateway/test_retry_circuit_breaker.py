"""Tests for RetryConfig, retry_async, and CircuitBreaker."""
from __future__ import annotations

import asyncio
from unittest.mock import AsyncMock, MagicMock

import pytest

from iot_edge_gateway.utils.circuit_breaker import (
    CircuitBreaker, CircuitBreakerOpenError, CircuitState,
)
from iot_edge_gateway.utils.retry import RetryConfig, retry_async


# ─────────────────── RetryConfig ───────────────────

class TestRetryConfig:
    def test_delay_increases_with_attempts(self):
        cfg = RetryConfig(base_delay_seconds=1.0, exponential_base=2.0, jitter=False)
        delays = [cfg.delay_for_attempt(i) for i in range(5)]
        for i in range(1, 5):
            assert delays[i] >= delays[i - 1]

    def test_max_delay_clamped(self):
        cfg = RetryConfig(base_delay_seconds=1.0, max_delay_seconds=5.0, exponential_base=10.0, jitter=False)
        assert cfg.delay_for_attempt(10) <= 5.0

    def test_jitter_within_bounds(self):
        cfg = RetryConfig(base_delay_seconds=1.0, max_delay_seconds=60.0, jitter=True)
        for _ in range(20):
            delay = cfg.delay_for_attempt(1)
            assert 0.5 <= delay <= 2.0 + 0.01  # jitter can scale up to 1.5× base


class TestRetryAsync:
    @pytest.mark.asyncio
    async def test_success_on_first_try(self):
        mock = AsyncMock(return_value="ok")
        result = await retry_async(mock, RetryConfig(max_attempts=3))
        assert result == "ok"
        assert mock.call_count == 1

    @pytest.mark.asyncio
    async def test_retries_and_succeeds(self):
        calls = []
        async def flaky():
            calls.append(1)
            if len(calls) < 3:
                raise ConnectionError("transient")
            return "done"
        cfg = RetryConfig(max_attempts=5, base_delay_seconds=0.01, jitter=False)
        result = await retry_async(flaky, cfg)
        assert result == "done"
        assert len(calls) == 3

    @pytest.mark.asyncio
    async def test_raises_after_max_attempts(self):
        async def always_fail():
            raise ValueError("permanent")
        cfg = RetryConfig(max_attempts=3, base_delay_seconds=0.01, jitter=False)
        with pytest.raises(ValueError, match="permanent"):
            await retry_async(always_fail, cfg)

    @pytest.mark.asyncio
    async def test_only_retries_configured_exceptions(self):
        calls = []
        async def raise_type_error():
            calls.append(1)
            raise TypeError("not retried")
        cfg = RetryConfig(
            max_attempts=3,
            base_delay_seconds=0.01,
            retryable_exceptions=(ValueError,),  # TypeError not in list
        )
        with pytest.raises(TypeError):
            await retry_async(raise_type_error, cfg)
        assert len(calls) == 1  # Did not retry


# ─────────────────── CircuitBreaker ───────────────────

class TestCircuitBreaker:
    @pytest.mark.asyncio
    async def test_closed_state_passes_calls(self):
        cb = CircuitBreaker("test", failure_threshold=3)
        mock = AsyncMock(return_value="result")
        result = await cb.call(mock)
        assert result == "result"
        assert cb.state == CircuitState.CLOSED

    @pytest.mark.asyncio
    async def test_opens_after_threshold_failures(self):
        cb = CircuitBreaker("test", failure_threshold=3, recovery_timeout=999)
        mock = AsyncMock(side_effect=RuntimeError("fail"))
        for _ in range(3):
            with pytest.raises(RuntimeError):
                await cb.call(mock)
        assert cb.state == CircuitState.OPEN

    @pytest.mark.asyncio
    async def test_open_raises_immediately(self):
        cb = CircuitBreaker("test", failure_threshold=1, recovery_timeout=999)
        mock = AsyncMock(side_effect=RuntimeError("fail"))
        with pytest.raises(RuntimeError):
            await cb.call(mock)
        assert cb.state == CircuitState.OPEN
        with pytest.raises(CircuitBreakerOpenError):
            await cb.call(mock)

    @pytest.mark.asyncio
    async def test_half_open_after_timeout(self):
        import time
        cb = CircuitBreaker("test", failure_threshold=1, recovery_timeout=0.05)
        mock = AsyncMock(side_effect=RuntimeError("fail"))
        with pytest.raises(RuntimeError):
            await cb.call(mock)
        assert cb.state == CircuitState.OPEN
        await asyncio.sleep(0.1)  # wait for recovery_timeout
        # Next call should transition to HALF_OPEN
        success_mock = AsyncMock(return_value="ok")
        result = await cb.call(success_mock)
        assert result == "ok"

    @pytest.mark.asyncio
    async def test_transitions_closed_after_successes(self):
        import time
        cb = CircuitBreaker("test", failure_threshold=1, recovery_timeout=0.05, success_threshold=2)
        fail = AsyncMock(side_effect=RuntimeError("fail"))
        ok = AsyncMock(return_value="ok")

        with pytest.raises(RuntimeError):
            await cb.call(fail)
        await asyncio.sleep(0.1)

        await cb.call(ok)   # HALF_OPEN success 1
        await cb.call(ok)   # HALF_OPEN success 2 → CLOSED
        assert cb.state == CircuitState.CLOSED

    @pytest.mark.asyncio
    async def test_reset_restores_closed(self):
        cb = CircuitBreaker("test", failure_threshold=1)
        mock = AsyncMock(side_effect=RuntimeError)
        with pytest.raises(RuntimeError):
            await cb.call(mock)
        assert cb.state == CircuitState.OPEN
        cb.reset()
        assert cb.state == CircuitState.CLOSED

    @pytest.mark.asyncio
    async def test_failure_count_resets_on_success(self):
        cb = CircuitBreaker("test", failure_threshold=5)
        fail = AsyncMock(side_effect=RuntimeError)
        ok = AsyncMock(return_value="ok")
        # Two failures then a success — should not open
        for _ in range(2):
            with pytest.raises(RuntimeError):
                await cb.call(fail)
        await cb.call(ok)
        assert cb.state == CircuitState.CLOSED
        assert cb._failure_count == 0

    def test_circuit_breaker_open_error_message(self):
        import time
        err = CircuitBreakerOpenError("mybreaker", time.monotonic() + 30)
        assert "mybreaker" in str(err)
        assert "OPEN" in str(err)
