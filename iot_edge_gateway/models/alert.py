from __future__ import annotations

import uuid
from datetime import datetime, timezone
from enum import Enum
from typing import Any, Optional

from pydantic import BaseModel, Field, field_validator


class AlertSeverity(str, Enum):
    INFO = "INFO"
    WARNING = "WARNING"
    CRITICAL = "CRITICAL"
    EMERGENCY = "EMERGENCY"


class AlertType(str, Enum):
    THRESHOLD_BREACH = "THRESHOLD_BREACH"
    RATE_OF_CHANGE = "RATE_OF_CHANGE"
    DEVICE_OFFLINE = "DEVICE_OFFLINE"
    DATA_QUALITY = "DATA_QUALITY"
    STALE_DATA = "STALE_DATA"
    PROTOCOL_ERROR = "PROTOCOL_ERROR"
    HUB_CONNECTIVITY = "HUB_CONNECTIVITY"
    VALIDATION_FAILURE = "VALIDATION_FAILURE"


class Alert(BaseModel):
    alert_id: str = Field(default_factory=lambda: str(uuid.uuid4()))
    device_id: str
    tag_name: str = ""
    alert_type: AlertType
    severity: AlertSeverity
    message: str
    value: Optional[float] = None
    threshold: Optional[float] = None
    timestamp: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    acknowledged: bool = False
    resolved: bool = False
    resolved_at: Optional[datetime] = None
    metadata: dict[str, Any] = Field(default_factory=dict)

    @field_validator("message")
    @classmethod
    def message_not_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("Alert message must not be empty")
        return v.strip()

    def acknowledge(self) -> None:
        self.acknowledged = True

    def resolve(self) -> None:
        self.resolved = True
        self.resolved_at = datetime.now(timezone.utc)

    def to_dict(self) -> dict[str, Any]:
        return {
            "alertId": self.alert_id,
            "deviceId": self.device_id,
            "tagName": self.tag_name,
            "alertType": self.alert_type.value,
            "severity": self.severity.value,
            "message": self.message,
            "value": self.value,
            "threshold": self.threshold,
            "timestamp": self.timestamp.isoformat(),
            "acknowledged": self.acknowledged,
            "resolved": self.resolved,
            "resolvedAt": self.resolved_at.isoformat() if self.resolved_at else None,
            "metadata": self.metadata,
        }
