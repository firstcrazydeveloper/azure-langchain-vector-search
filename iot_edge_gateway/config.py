from __future__ import annotations

import os
from typing import Any, Optional

from pydantic import Field, field_validator, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class MQTTConfig(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="MQTT_", extra="ignore")

    broker_host: str = Field(default="localhost")
    broker_port: int = Field(default=1883, ge=1, le=65535)
    tls_enabled: bool = Field(default=False)
    ca_cert_path: str = Field(default="")
    client_cert_path: str = Field(default="")
    client_key_path: str = Field(default="")
    username: str = Field(default="")
    password: str = Field(default="")
    client_id: str = Field(default="iot-edge-gateway")
    keepalive_seconds: int = Field(default=60, ge=10, le=3600)
    qos: int = Field(default=1, ge=0, le=2)
    topics: list[str] = Field(default_factory=lambda: ["devices/#"])
    reconnect_delay_seconds: float = Field(default=5.0, ge=1.0)
    max_reconnect_attempts: int = Field(default=10, ge=1)


class OPCUAConfig(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="OPCUA_", extra="ignore")

    endpoint_url: str = Field(default="opc.tcp://localhost:4840/freeopcua/server/")
    security_policy: str = Field(default="NoSecurity")
    security_mode: str = Field(default="None")
    certificate_path: str = Field(default="")
    private_key_path: str = Field(default="")
    username: str = Field(default="")
    password: str = Field(default="")
    session_timeout_ms: int = Field(default=30000, ge=1000)
    subscription_interval_ms: float = Field(default=1000.0, ge=100.0)
    node_ids: list[str] = Field(default_factory=list)
    namespace_uri: str = Field(default="")
    reconnect_delay_seconds: float = Field(default=5.0, ge=1.0)


class ModbusConfig(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="MODBUS_", extra="ignore")

    host: str = Field(default="localhost")
    port: int = Field(default=502, ge=1, le=65535)
    unit_id: int = Field(default=1, ge=0, le=247)
    timeout_seconds: float = Field(default=3.0, ge=0.1, le=30.0)
    poll_interval_seconds: float = Field(default=1.0, ge=0.1)
    coil_addresses: list[int] = Field(default_factory=list)
    discrete_input_addresses: list[int] = Field(default_factory=list)
    holding_register_addresses: list[int] = Field(default_factory=list)
    input_register_addresses: list[int] = Field(default_factory=list)
    max_registers_per_request: int = Field(default=125, ge=1, le=125)
    reconnect_delay_seconds: float = Field(default=5.0, ge=1.0)


class ValidationThreshold(BaseSettings):
    model_config = SettingsConfigDict(extra="ignore")

    tag_name: str
    min_value: Optional[float] = None
    max_value: Optional[float] = None
    max_rate_of_change: Optional[float] = None
    stale_threshold_seconds: float = Field(default=300.0, ge=1.0)
    critical_min: Optional[float] = None
    critical_max: Optional[float] = None


class GatewayConfig(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="GATEWAY_", extra="ignore")

    gateway_id: str = Field(default="edge-gateway-01")
    iothub_connection_string: str = Field(default="")
    iothub_module_connection_string: str = Field(default="")
    telemetry_batch_size: int = Field(default=50, ge=1, le=500)
    telemetry_batch_interval_seconds: float = Field(default=5.0, ge=0.5)
    max_queue_size: int = Field(default=10000, ge=100)
    message_size_limit_bytes: int = Field(default=262144, ge=1024)  # 256 KB IoT Hub limit
    alert_cooldown_seconds: float = Field(default=60.0, ge=0.0)
    enable_local_storage: bool = Field(default=True)
    local_storage_path: str = Field(default="/tmp/iot_edge_gateway/readings")
    log_level: str = Field(default="INFO")
    enable_mqtt: bool = Field(default=True)
    enable_opcua: bool = Field(default=False)
    enable_modbus: bool = Field(default=False)
    deadband_percent: float = Field(default=0.1, ge=0.0, le=100.0)
    c2d_timeout_seconds: int = Field(default=30, ge=1)

    @field_validator("log_level")
    @classmethod
    def valid_log_level(cls, v: str) -> str:
        valid = {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}
        if v.upper() not in valid:
            raise ValueError(f"log_level must be one of {valid}")
        return v.upper()


class EdgeGatewaySettings:
    """Aggregated settings for the full gateway."""

    def __init__(self) -> None:
        self.gateway = GatewayConfig()
        self.mqtt = MQTTConfig()
        self.opcua = OPCUAConfig()
        self.modbus = ModbusConfig()
        self.thresholds: list[ValidationThreshold] = self._load_thresholds()

    def _load_thresholds(self) -> list[ValidationThreshold]:
        # Thresholds are loaded from environment JSON or can be injected via module twin.
        import json
        raw = os.getenv("VALIDATION_THRESHOLDS_JSON", "[]")
        try:
            items = json.loads(raw)
            return [ValidationThreshold(**item) for item in items]
        except Exception:
            return []

    def get_threshold(self, tag_name: str) -> Optional[ValidationThreshold]:
        for t in self.thresholds:
            if t.tag_name == tag_name:
                return t
        return None
