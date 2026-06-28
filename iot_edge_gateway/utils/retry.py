from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass, field
from typing import Any, Callable, Optional, Tuple, Type

logger = logging.getLogger(__name__)


@dataclass
class RetryConfig:
    max_attempts: int = 5
    base_delay_seconds: float = 1.0
    max_delay_seconds: float = 60.0
    exponential_base: float = 2.0
    jitter: bool = True
    retryable_exceptions: Tuple[Type[Exception], ...] = field(
        default_factory=lambda: (Exception,)
    )

    def delay_for_attempt(self, attempt: int) -> float:
        import random
        delay = min(
            self.base_delay_seconds * (self.exponential_base ** attempt),
            self.max_delay_seconds,
        )
        if self.jitter:
            delay *= 0.5 + random.random() * 0.5
        return delay


async def retry_async(
    func: Callable[..., Any],
    config: Optional[RetryConfig] = None,
    *args: Any,
    **kwargs: Any,
) -> Any:
    cfg = config or RetryConfig()
    last_exception: Optional[Exception] = None

    for attempt in range(cfg.max_attempts):
        try:
            return await func(*args, **kwargs)
        except cfg.retryable_exceptions as exc:
            last_exception = exc
            if attempt < cfg.max_attempts - 1:
                delay = cfg.delay_for_attempt(attempt)
                logger.warning(
                    "Attempt %d/%d failed: %s. Retrying in %.2fs.",
                    attempt + 1, cfg.max_attempts, exc, delay,
                )
                await asyncio.sleep(delay)
            else:
                logger.error(
                    "All %d attempts failed. Last error: %s",
                    cfg.max_attempts, exc,
                )

    raise last_exception  # type: ignore[misc]
