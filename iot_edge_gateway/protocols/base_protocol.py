from __future__ import annotations

import asyncio
import logging
from abc import ABC, abstractmethod
from typing import Callable, Coroutine, Optional

from ..models import DeviceReading

logger = logging.getLogger(__name__)


class ProtocolError(Exception):
    """Raised when a protocol operation fails in an unrecoverable way."""


DataCallback = Callable[[DeviceReading], Coroutine]


class BaseProtocol(ABC):
    """Abstract base class that all protocol adapters must implement."""

    def __init__(self, device_id: str) -> None:
        self.device_id = device_id
        self._connected = False
        self._data_callback: Optional[DataCallback] = None
        self._running = False

    @property
    def is_connected(self) -> bool:
        return self._connected

    def set_data_callback(self, callback: DataCallback) -> None:
        self._data_callback = callback

    async def _emit(self, reading: DeviceReading) -> None:
        if self._data_callback is not None:
            try:
                await self._data_callback(reading)
            except Exception as exc:
                logger.error("Data callback raised: %s", exc, exc_info=True)

    @abstractmethod
    async def connect(self) -> None:
        """Establish connection to the device/broker."""

    @abstractmethod
    async def disconnect(self) -> None:
        """Gracefully close the connection."""

    @abstractmethod
    async def start_reading(self) -> None:
        """Start continuously reading data and emitting via callback."""

    @abstractmethod
    async def write_value(self, tag_name: str, value: object) -> bool:
        """Write a value to the device. Returns True on success."""

    async def reconnect(self, max_attempts: int = 5, delay: float = 5.0) -> None:
        for attempt in range(1, max_attempts + 1):
            try:
                logger.info(
                    "[%s] Reconnect attempt %d/%d...",
                    self.__class__.__name__, attempt, max_attempts,
                )
                await self.connect()
                logger.info("[%s] Reconnected successfully.", self.__class__.__name__)
                return
            except Exception as exc:
                logger.warning(
                    "[%s] Reconnect attempt %d failed: %s",
                    self.__class__.__name__, attempt, exc,
                )
                if attempt < max_attempts:
                    await asyncio.sleep(delay * attempt)
        raise ProtocolError(
            f"[{self.__class__.__name__}] Failed to reconnect after {max_attempts} attempts."
        )
