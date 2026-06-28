from __future__ import annotations

import asyncio
import logging
from datetime import datetime, timezone
from typing import Any, Optional

from ..config import ModbusConfig
from ..models import DeviceReading, DataQuality, ProtocolType
from .base_protocol import BaseProtocol, ProtocolError

logger = logging.getLogger(__name__)

_REGISTER_MAX = 65535
_SIGNED_THRESHOLD = 32767


class ModbusTCPProtocol(BaseProtocol):
    """Modbus TCP protocol adapter using pymodbus."""

    def __init__(self, config: ModbusConfig, device_id: str = "modbus-device") -> None:
        super().__init__(device_id)
        self.config = config
        self._client: Optional[Any] = None
        self._poll_task: Optional[asyncio.Task] = None

    async def connect(self) -> None:
        try:
            from pymodbus.client import AsyncModbusTcpClient
        except ImportError as exc:
            raise ProtocolError("pymodbus is not installed. Run: pip install pymodbus") from exc

        client = AsyncModbusTcpClient(
            host=self.config.host,
            port=self.config.port,
            timeout=self.config.timeout_seconds,
        )
        connected = await client.connect()
        if not connected:
            raise ProtocolError(
                f"Modbus TCP could not connect to {self.config.host}:{self.config.port}"
            )
        self._client = client
        self._connected = True
        logger.info("Modbus TCP connected to %s:%d (unit=%d).", self.config.host, self.config.port, self.config.unit_id)

    async def disconnect(self) -> None:
        self._running = False
        if self._poll_task:
            self._poll_task.cancel()
            try:
                await self._poll_task
            except asyncio.CancelledError:
                pass
        if self._client:
            self._client.close()
            self._client = None
        self._connected = False
        logger.info("Modbus TCP disconnected.")

    async def start_reading(self) -> None:
        if not self._client:
            raise ProtocolError("Modbus TCP not connected.")
        self._running = True
        self._poll_task = asyncio.create_task(self._poll_loop())
        logger.info("Modbus TCP polling started.")

    async def _poll_loop(self) -> None:
        while self._running:
            try:
                await self._poll_all_registers()
                await asyncio.sleep(self.config.poll_interval_seconds)
            except asyncio.CancelledError:
                break
            except Exception as exc:
                logger.warning("Modbus poll error: %s. Reconnecting.", exc)
                self._connected = False
                try:
                    await self.reconnect(max_attempts=5, delay=self.config.reconnect_delay_seconds)
                except ProtocolError as exc:
                    logger.critical("Modbus TCP permanent failure: %s", exc)
                    self._running = False

    async def _poll_all_registers(self) -> None:
        now = datetime.now(timezone.utc)

        # Coils (Read Coils - FC01)
        for addr in self.config.coil_addresses:
            await self._read_coils(addr, now)

        # Discrete Inputs (Read Discrete Inputs - FC02)
        for addr in self.config.discrete_input_addresses:
            await self._read_discrete_inputs(addr, now)

        # Holding Registers (Read Holding Registers - FC03)
        await self._read_registers_chunked(
            self.config.holding_register_addresses, "holding", now
        )

        # Input Registers (Read Input Registers - FC04)
        await self._read_registers_chunked(
            self.config.input_register_addresses, "input", now
        )

    async def _read_coils(self, address: int, now: datetime) -> None:
        if not self._client:
            return
        try:
            result = await self._client.read_coils(address, count=1, slave=self.config.unit_id)
            if result.isError():
                logger.warning("Modbus coil read error at address %d.", address)
                quality = DataQuality.BAD
                raw = None
            else:
                raw = bool(result.bits[0])
                quality = DataQuality.GOOD
            await self._emit_reading(f"coil_{address}", raw, float(raw) if raw is not None else None, quality, now)
        except Exception as exc:
            logger.error("Modbus read_coils[%d] failed: %s", address, exc)

    async def _read_discrete_inputs(self, address: int, now: datetime) -> None:
        if not self._client:
            return
        try:
            result = await self._client.read_discrete_inputs(address, count=1, slave=self.config.unit_id)
            if result.isError():
                logger.warning("Modbus discrete input error at address %d.", address)
                quality = DataQuality.BAD
                raw = None
            else:
                raw = bool(result.bits[0])
                quality = DataQuality.GOOD
            await self._emit_reading(f"discrete_{address}", raw, float(raw) if raw is not None else None, quality, now)
        except Exception as exc:
            logger.error("Modbus read_discrete_inputs[%d] failed: %s", address, exc)

    async def _read_registers_chunked(
        self, addresses: list[int], reg_type: str, now: datetime
    ) -> None:
        if not addresses:
            return
        chunks = _chunk_addresses(sorted(addresses), self.config.max_registers_per_request)
        for chunk in chunks:
            start = chunk[0]
            count = chunk[-1] - chunk[0] + 1
            try:
                if reg_type == "holding":
                    result = await self._client.read_holding_registers(start, count=count, slave=self.config.unit_id)
                else:
                    result = await self._client.read_input_registers(start, count=count, slave=self.config.unit_id)

                if result.isError():
                    logger.warning("Modbus %s registers error at %d.", reg_type, start)
                    for addr in chunk:
                        await self._emit_reading(f"{reg_type}_{addr}", None, None, DataQuality.BAD, now)
                    continue

                for addr in chunk:
                    offset = addr - start
                    if offset < len(result.registers):
                        raw_reg = result.registers[offset]
                        # Convert unsigned 16-bit to signed
                        signed = raw_reg if raw_reg <= _SIGNED_THRESHOLD else raw_reg - 65536
                        await self._emit_reading(
                            f"{reg_type}_{addr}", raw_reg, float(signed), DataQuality.GOOD, now
                        )
            except Exception as exc:
                logger.error("Modbus read_%s_registers chunk %d-%d failed: %s", reg_type, start, start + count - 1, exc)

    async def _emit_reading(
        self,
        tag_name: str,
        raw: Any,
        translated: Optional[float],
        quality: DataQuality,
        now: datetime,
    ) -> None:
        try:
            reading = DeviceReading(
                device_id=self.device_id,
                protocol=ProtocolType.MODBUS_TCP,
                tag_name=tag_name,
                raw_value=raw,
                translated_value=translated,
                quality=quality,
                timestamp=now,
                metadata={
                    "host": self.config.host,
                    "port": self.config.port,
                    "unitId": self.config.unit_id,
                },
            )
            await self._emit(reading)
        except Exception as exc:
            logger.warning("Failed to create Modbus DeviceReading for tag %s: %s", tag_name, exc)

    async def write_value(self, tag_name: str, value: object) -> bool:
        if not self._client or not self._connected:
            logger.error("Modbus TCP not connected — cannot write to %s.", tag_name)
            return False

        try:
            parts = str(tag_name).split("_")
            reg_type = parts[0]
            address = int(parts[1])
        except (IndexError, ValueError) as exc:
            logger.error("Invalid Modbus tag format '%s'. Expected '<type>_<address>': %s", tag_name, exc)
            return False

        try:
            if reg_type == "coil":
                result = await self._client.write_coil(address, bool(value), slave=self.config.unit_id)
            elif reg_type == "holding":
                int_val = int(value)  # type: ignore[arg-type]
                if not (0 <= int_val <= _REGISTER_MAX):
                    logger.error("Modbus register value %d out of range [0, %d].", int_val, _REGISTER_MAX)
                    return False
                result = await self._client.write_register(address, int_val, slave=self.config.unit_id)
            else:
                logger.error("Modbus write not supported for register type '%s'.", reg_type)
                return False

            if result.isError():
                logger.error("Modbus write error on %s.", tag_name)
                return False
            logger.info("Modbus wrote %s = %s", tag_name, value)
            return True
        except Exception as exc:
            logger.error("Modbus write failed for %s: %s", tag_name, exc)
            return False

    async def read_tag(self, tag_name: str) -> Optional[Any]:
        """One-shot read of a single tag."""
        if not self._client or not self._connected:
            return None
        try:
            parts = str(tag_name).split("_")
            reg_type = parts[0]
            address = int(parts[1])
        except (IndexError, ValueError):
            return None

        try:
            if reg_type == "coil":
                r = await self._client.read_coils(address, count=1, slave=self.config.unit_id)
                return bool(r.bits[0]) if not r.isError() else None
            elif reg_type == "discrete":
                r = await self._client.read_discrete_inputs(address, count=1, slave=self.config.unit_id)
                return bool(r.bits[0]) if not r.isError() else None
            elif reg_type == "holding":
                r = await self._client.read_holding_registers(address, count=1, slave=self.config.unit_id)
                return r.registers[0] if not r.isError() else None
            elif reg_type == "input":
                r = await self._client.read_input_registers(address, count=1, slave=self.config.unit_id)
                return r.registers[0] if not r.isError() else None
        except Exception as exc:
            logger.error("Modbus read_tag failed for %s: %s", tag_name, exc)
        return None


def _chunk_addresses(sorted_addresses: list[int], max_count: int) -> list[list[int]]:
    """Group consecutive addresses into chunks of at most max_count."""
    if not sorted_addresses:
        return []
    chunks: list[list[int]] = []
    current_chunk: list[int] = [sorted_addresses[0]]
    for addr in sorted_addresses[1:]:
        span = addr - current_chunk[0] + 1
        if span <= max_count and addr == current_chunk[-1] + 1:
            current_chunk.append(addr)
        else:
            chunks.append(current_chunk)
            current_chunk = [addr]
    chunks.append(current_chunk)
    return chunks
