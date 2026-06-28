from __future__ import annotations

import asyncio
import logging
import time
from enum import Enum
from typing import Any, Callable, Optional

logger = logging.getLogger(__name__)


class CircuitState(str, Enum):
    CLOSED = "CLOSED"       # Normal operation, requests pass through
    OPEN = "OPEN"           # Failing, requests rejected immediately
    HALF_OPEN = "HALF_OPEN" # Testing if service recovered


class CircuitBreakerOpenError(Exception):
    def __init__(self, name: str, until: float) -> None:
        self.name = name
        self.until = until
        remaining = max(0.0, until - time.monotonic())
        super().__init__(f"Circuit '{name}' is OPEN. Retrying in {remaining:.1f}s.")


class CircuitBreaker:
    def __init__(
        self,
        name: str,
        failure_threshold: int = 5,
        recovery_timeout: float = 30.0,
        success_threshold: int = 2,
    ) -> None:
        self.name = name
        self.failure_threshold = failure_threshold
        self.recovery_timeout = recovery_timeout
        self.success_threshold = success_threshold

        self._state = CircuitState.CLOSED
        self._failure_count = 0
        self._success_count = 0
        self._opened_at: float = 0.0
        self._lock = asyncio.Lock()

    @property
    def state(self) -> CircuitState:
        return self._state

    async def call(self, func: Callable[..., Any], *args: Any, **kwargs: Any) -> Any:
        async with self._lock:
            await self._maybe_transition()

            if self._state == CircuitState.OPEN:
                raise CircuitBreakerOpenError(self.name, self._opened_at + self.recovery_timeout)

        try:
            result = await func(*args, **kwargs)
            async with self._lock:
                await self._on_success()
            return result
        except Exception as exc:
            async with self._lock:
                await self._on_failure()
            raise exc

    async def _maybe_transition(self) -> None:
        if self._state == CircuitState.OPEN:
            elapsed = time.monotonic() - self._opened_at
            if elapsed >= self.recovery_timeout:
                logger.info("Circuit '%s' transitioning OPEN → HALF_OPEN.", self.name)
                self._state = CircuitState.HALF_OPEN
                self._success_count = 0

    async def _on_success(self) -> None:
        if self._state == CircuitState.HALF_OPEN:
            self._success_count += 1
            if self._success_count >= self.success_threshold:
                logger.info("Circuit '%s' transitioning HALF_OPEN → CLOSED.", self.name)
                self._state = CircuitState.CLOSED
                self._failure_count = 0
        elif self._state == CircuitState.CLOSED:
            self._failure_count = 0

    async def _on_failure(self) -> None:
        self._failure_count += 1
        logger.warning(
            "Circuit '%s' failure %d/%d in state %s.",
            self.name, self._failure_count, self.failure_threshold, self._state.value,
        )
        if self._state in (CircuitState.CLOSED, CircuitState.HALF_OPEN):
            if (
                self._state == CircuitState.HALF_OPEN
                or self._failure_count >= self.failure_threshold
            ):
                logger.error("Circuit '%s' transitioning → OPEN.", self.name)
                self._state = CircuitState.OPEN
                self._opened_at = time.monotonic()
                self._failure_count = 0

    def reset(self) -> None:
        self._state = CircuitState.CLOSED
        self._failure_count = 0
        self._success_count = 0
