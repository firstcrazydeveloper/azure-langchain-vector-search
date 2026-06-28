from __future__ import annotations

import uuid
from datetime import datetime, timezone
from enum import Enum
from typing import Any, Optional

from pydantic import BaseModel, Field, field_validator, model_validator


class ProtocolType(str, Enum):
    MQTT = "MQTT"
    OPC_UA = "OPC_UA"
    MODBUS_TCP = "MODBUS_TCP"


class DataQuality(str, Enum):
    GOOD = "GOOD"
    BAD = "BAD"
    UNCERTAIN = "UNCERTAIN"
    STALE = "STALE"


class DeviceReading(BaseModel):
    reading_id: str = Field(default_factory=lambda: str(uuid.uuid4()))
    device_id: str
    device_name: str = ""
    protocol: ProtocolType
    tag_name: str
    raw_value: Any
    translated_value: Optional[float] = None
    unit: str = ""
    quality: DataQuality = DataQuality.GOOD
    timestamp: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    source_timestamp: Optional[datetime] = None
    metadata: dict[str, Any] = Field(default_factory=dict)
    is_filtered: bool = False
    filter_reason: str = ""

    @field_validator("device_id")
    @classmethod
    def device_id_not_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("device_id must not be empty")
        return v.strip()

    @field_validator("tag_name")
    @classmethod
    def tag_name_not_empty(cls, v: str) -> str:
        if not v or not v.strip():
            raise ValueError("tag_name must not be empty")
        return v.strip()

    @field_validator("translated_value")
    @classmethod
    def translated_value_finite(cls, v: Optional[float]) -> Optional[float]:
        if v is not None:
            import math
            if math.isnan(v) or math.isinf(v):
                raise ValueError("translated_value must be a finite number")
        return v

    @model_validator(mode="after")
    def source_timestamp_not_future(self) -> "DeviceReading":
        if self.source_timestamp is not None:
            now = datetime.now(timezone.utc)
            if self.source_timestamp > now:
                self.quality = DataQuality.UNCERTAIN
                self.metadata["timestamp_warning"] = "source_timestamp is in the future"
        return self

    def to_telemetry_dict(self) -> dict[str, Any]:
        return {
            "readingId": self.reading_id,
            "deviceId": self.device_id,
            "deviceName": self.device_name,
            "protocol": self.protocol.value,
            "tagName": self.tag_name,
            "value": self.translated_value if self.translated_value is not None else self.raw_value,
            "unit": self.unit,
            "quality": self.quality.value,
            "timestamp": self.timestamp.isoformat(),
            "sourceTimestamp": self.source_timestamp.isoformat() if self.source_timestamp else None,
            "metadata": self.metadata,
        }


class TelemetryBatch(BaseModel):
    batch_id: str = Field(default_factory=lambda: str(uuid.uuid4()))
    gateway_id: str
    readings: list[DeviceReading] = Field(default_factory=list)
    created_at: datetime = Field(default_factory=lambda: datetime.now(timezone.utc))
    sequence_number: int = 0

    @field_validator("readings")
    @classmethod
    def readings_not_empty(cls, v: list[DeviceReading]) -> list[DeviceReading]:
        if not v:
            raise ValueError("TelemetryBatch must contain at least one reading")
        return v

    def to_iothub_message(self) -> dict[str, Any]:
        return {
            "batchId": self.batch_id,
            "gatewayId": self.gateway_id,
            "sequenceNumber": self.sequence_number,
            "createdAt": self.created_at.isoformat(),
            "count": len(self.readings),
            "readings": [r.to_telemetry_dict() for r in self.readings],
        }
