from __future__ import annotations

import asyncio
import json
import logging
from typing import Any, Callable, Coroutine, Optional

from ..models import C2DCommand, CommandStatus, CommandType
from ..protocols.base_protocol import BaseProtocol

logger = logging.getLogger(__name__)

CommandExecutor = Callable[[C2DCommand], Coroutine[Any, Any, dict[str, Any]]]


class C2DCommandHandler:
    """
    Handles Cloud-to-Device (C2D) commands received from Azure IoT Hub.
    Supports direct methods, desired property updates, and message-based C2D.
    """

    def __init__(
        self,
        protocols: dict[str, BaseProtocol],
        timeout_seconds: int = 30,
    ) -> None:
        self._protocols = protocols
        self._timeout_seconds = timeout_seconds
        self._custom_executors: dict[CommandType, CommandExecutor] = {}
        self._pending_commands: dict[str, C2DCommand] = {}
        self._command_history: list[C2DCommand] = []
        self._client: Optional[Any] = None

    def register_executor(self, command_type: CommandType, executor: CommandExecutor) -> None:
        self._custom_executors[command_type] = executor

    async def attach_to_client(self, client: Any) -> None:
        """Register IoT Hub client callbacks for direct methods and C2D messages."""
        self._client = client
        if hasattr(client, "on_method_request_received"):
            client.on_method_request_received = self._on_direct_method
        if hasattr(client, "on_message_received"):
            client.on_message_received = self._on_c2d_message
        logger.info("C2D handler attached to IoT Hub client.")

    async def _on_direct_method(self, method_request: Any) -> None:
        """Called when a direct method invocation arrives from IoT Hub."""
        logger.info("Direct method received: %s", method_request.name)
        try:
            payload = json.loads(method_request.payload) if method_request.payload else {}
        except (json.JSONDecodeError, TypeError):
            payload = {}

        command = self._parse_command_from_method(method_request.name, payload)
        result = await self._execute_command(command)

        if self._client and hasattr(self._client, "send_method_response"):
            from azure.iot.device import MethodResponse
            status = 200 if command.status == CommandStatus.SUCCESS else 500
            response = MethodResponse.create_from_method_request(
                method_request, status, result
            )
            try:
                await self._client.send_method_response(response)
            except Exception as exc:
                logger.error("Failed to send direct method response: %s", exc)

    async def _on_c2d_message(self, message: Any) -> None:
        """Called when a C2D message arrives."""
        try:
            data = json.loads(str(message.data, "utf-8") if isinstance(message.data, bytes) else message.data)
        except (json.JSONDecodeError, TypeError, AttributeError):
            logger.warning("C2D message has invalid JSON payload — ignoring.")
            return

        command = self._parse_command_from_message(data)
        if command is None:
            logger.warning("C2D message could not be parsed into a command: %s", data)
            return

        await self._execute_command(command)

    async def execute_command(self, command: C2DCommand) -> dict[str, Any]:
        """Public entry point for programmatic command submission."""
        return await self._execute_command(command)

    async def _execute_command(self, command: C2DCommand) -> dict[str, Any]:
        if command.is_expired():
            command.mark_timeout()
            self._record(command)
            return command.to_response_dict()

        self._pending_commands[command.command_id] = command
        command.mark_executing()

        try:
            result = await asyncio.wait_for(
                self._dispatch(command),
                timeout=self._timeout_seconds,
            )
            command.mark_success(result)
        except asyncio.TimeoutError:
            command.mark_timeout()
            logger.error("Command %s timed out.", command.command_id)
        except Exception as exc:
            command.mark_failed(str(exc))
            logger.error("Command %s failed: %s", command.command_id, exc, exc_info=True)
        finally:
            self._pending_commands.pop(command.command_id, None)
            self._record(command)

        return command.to_response_dict()

    async def _dispatch(self, command: C2DCommand) -> dict[str, Any]:
        if command.command_type in self._custom_executors:
            return await self._custom_executors[command.command_type](command)

        protocol = self._get_protocol_for_device(command.target_device_id)
        if protocol is None:
            raise ValueError(f"No protocol found for device '{command.target_device_id}'")

        if command.command_type == CommandType.WRITE_TAG:
            return await self._handle_write_tag(command, protocol)
        if command.command_type == CommandType.READ_TAG:
            return await self._handle_read_tag(command, protocol)
        if command.command_type == CommandType.SET_POINT:
            return await self._handle_set_point(command, protocol)
        if command.command_type == CommandType.START_DEVICE:
            return await self._handle_start_stop(command, protocol, start=True)
        if command.command_type == CommandType.STOP_DEVICE:
            return await self._handle_start_stop(command, protocol, start=False)
        if command.command_type == CommandType.RESET_DEVICE:
            return await self._handle_reset(command, protocol)

        raise ValueError(f"Unsupported command type: {command.command_type}")

    async def _handle_write_tag(self, command: C2DCommand, protocol: BaseProtocol) -> dict[str, Any]:
        tag = command.parameters.get("tagName")
        value = command.parameters.get("value")
        if tag is None or value is None:
            raise ValueError("WRITE_TAG requires 'tagName' and 'value' in parameters.")
        success = await protocol.write_value(tag, value)
        if not success:
            raise RuntimeError(f"Write failed for tag '{tag}'.")
        return {"written": True, "tagName": tag, "value": value}

    async def _handle_read_tag(self, command: C2DCommand, protocol: BaseProtocol) -> dict[str, Any]:
        tag = command.parameters.get("tagName")
        if not tag:
            raise ValueError("READ_TAG requires 'tagName' in parameters.")
        if not hasattr(protocol, "read_tag"):
            raise ValueError("Protocol does not support read_tag.")
        value = await protocol.read_tag(tag)  # type: ignore[attr-defined]
        return {"tagName": tag, "value": value}

    async def _handle_set_point(self, command: C2DCommand, protocol: BaseProtocol) -> dict[str, Any]:
        tag = command.parameters.get("tagName")
        value = command.parameters.get("setpoint")
        if tag is None or value is None:
            raise ValueError("SET_POINT requires 'tagName' and 'setpoint' parameters.")
        success = await protocol.write_value(tag, float(value))
        if not success:
            raise RuntimeError(f"Set-point write failed for tag '{tag}'.")
        return {"setpoint": float(value), "tagName": tag, "applied": True}

    async def _handle_start_stop(
        self, command: C2DCommand, protocol: BaseProtocol, start: bool
    ) -> dict[str, Any]:
        tag = command.parameters.get("controlTag", "coil_0")
        success = await protocol.write_value(tag, start)
        action = "started" if start else "stopped"
        if not success:
            raise RuntimeError(f"Device {action} command failed on tag '{tag}'.")
        return {"device": command.target_device_id, "action": action}

    async def _handle_reset(self, command: C2DCommand, protocol: BaseProtocol) -> dict[str, Any]:
        tag = command.parameters.get("resetTag", "coil_1")
        await protocol.write_value(tag, True)
        await asyncio.sleep(0.5)
        await protocol.write_value(tag, False)
        return {"device": command.target_device_id, "action": "reset"}

    def _get_protocol_for_device(self, device_id: str) -> Optional[BaseProtocol]:
        return self._protocols.get(device_id) or (
            next(iter(self._protocols.values())) if self._protocols else None
        )

    def _parse_command_from_method(self, method_name: str, payload: dict) -> C2DCommand:
        try:
            cmd_type = CommandType(method_name.upper())
        except ValueError:
            cmd_type = CommandType.WRITE_TAG  # fallback

        return C2DCommand(
            target_device_id=payload.get("deviceId", "default"),
            command_type=cmd_type,
            parameters=payload,
            timeout_seconds=payload.get("timeoutSeconds", self._timeout_seconds),
        )

    def _parse_command_from_message(self, data: dict) -> Optional[C2DCommand]:
        try:
            cmd_type_str = data.get("commandType", data.get("command", "")).upper()
            cmd_type = CommandType(cmd_type_str)
            return C2DCommand(
                target_device_id=data.get("deviceId", "default"),
                command_type=cmd_type,
                parameters=data.get("parameters", {}),
                timeout_seconds=data.get("timeoutSeconds", self._timeout_seconds),
                correlation_id=data.get("correlationId", ""),
            )
        except (ValueError, KeyError) as exc:
            logger.warning("Cannot parse C2D message into command: %s", exc)
            return None

    def _record(self, command: C2DCommand) -> None:
        self._command_history.append(command)
        if len(self._command_history) > 1000:
            self._command_history = self._command_history[-500:]

    def get_pending_commands(self) -> list[C2DCommand]:
        return list(self._pending_commands.values())

    def get_command_history(self, limit: int = 50) -> list[C2DCommand]:
        return self._command_history[-limit:]
