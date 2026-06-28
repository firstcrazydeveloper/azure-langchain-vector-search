from __future__ import annotations

import uuid
from datetime import datetime, timezone
from enum import Enum
from typing import Any, Optional

from pydantic import BaseModel, Field, field_validator


class CommandType(str, Enum):
    SET_POINT = "SET_POINT"
    START_DEVICE = "START_DEVICE"
    STOP_DEVICE = "STOP_DEVICE"
    RESET_DEVICE = "RESET_DEVICE"
    ACKNOWLEDGE_ALERT = "ACKNOWLEDGE_ALERT"
    UPDATE_CONFIG = "UPDATE_CONFIG"
    READ_TAG = "READ_TAG"
    WRITE_TAG = "WRITE_TAG"


class CommandStatus(str, Enum):
    PENDING = "PENDING"
    EXECUTING = "EXECUTING"
    SUCCESS = "SUCCESS"
    FAILED = "FAILED"
    TIMEOUT = "TIMEOUT"
    REJECTED = "REJECTED"


class C2DCommand(BaseModel):
    command_id: str = Field(default_factory=lambda: str(uuid.uuid4()))
    target_device_id: str
    command_type: CommandType
    parameters: dict[str, Any] = Field(default_factory=dict)
    status: CommandStatus = CommandStatus.PENDING
    issued_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    executed_at: Optional[datetime] = None
    timeout_seconds: int = Field(default=30, ge=1, le=3600)
    result: Optional[dict[str, Any]] = None
    error_message: str = ""
    correlation_id: str = Field(default_factory=lambda: str(uuid.uuid4()))

    @field_validator("target_device_id")
    @classmethod
    def target_device_id_not_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("target_device_id must not be empty")
        return v.strip()

    def mark_executing(self) -> None:
        self.status = CommandStatus.EXECUTING
        self.executed_at = datetime.now(timezone.utc)

    def mark_success(self, result: dict[str, Any]) -> None:
        self.status = CommandStatus.SUCCESS
        self.result = result

    def mark_failed(self, error: str) -> None:
        self.status = CommandStatus.FAILED
        self.error_message = error

    def mark_timeout(self) -> None:
        self.status = CommandStatus.TIMEOUT
        self.error_message = f"Command timed out after {self.timeout_seconds}s"

    def mark_rejected(self, reason: str) -> None:
        self.status = CommandStatus.REJECTED
        self.error_message = reason

    def is_expired(self) -> bool:
        from datetime import timedelta
        elapsed = datetime.now(timezone.utc) - self.issued_at
        return elapsed.total_seconds() > self.timeout_seconds

    def to_response_dict(self) -> dict[str, Any]:
        return {
            "commandId": self.command_id,
            "correlationId": self.correlation_id,
            "targetDeviceId": self.target_device_id,
            "commandType": self.command_type.value,
            "status": self.status.value,
            "issuedAt": self.issued_at.isoformat(),
            "executedAt": self.executed_at.isoformat() if self.executed_at else None,
            "result": self.result,
            "errorMessage": self.error_message,
        }
