"""
IoT Edge Gateway Module
=======================
Implements MQTT, OPC UA, and Modbus TCP protocol adapters with a
translate → filter → validate → alert → IoT Hub pipeline.
Cloud-to-Device (C2D) command routing back to field devices is also supported.
"""

from .gateway import EdgeGateway
from .config import EdgeGatewaySettings

__all__ = ["EdgeGateway", "EdgeGatewaySettings"]
__version__ = "1.0.0"
