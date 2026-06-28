using System.Collections.Generic;

namespace IoTEdgeGateway.Infrastructure.Configuration
{
    public sealed class GatewayConfiguration
    {
        public MqttAdapterConfig Mqtt { get; set; } = new();
        public OpcUaAdapterConfig OpcUa { get; set; } = new();
        public ModbusAdapterConfig Modbus { get; set; } = new();
        public IoTHubConfig IoTHub { get; set; } = new();
        public AlertConfig Alert { get; set; } = new();
    }

    public sealed class MqttAdapterConfig
    {
        public bool Enabled { get; set; } = true;
        public string BrokerHost { get; set; } = "localhost";
        public int BrokerPort { get; set; } = 1883;
        public string ClientId { get; set; } = "iot-edge-gateway";
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool UseTls { get; set; } = false;
        public List<string> TopicsToSubscribe { get; set; } = new() { "devices/+/telemetry" };
        public int KeepAliveSeconds { get; set; } = 30;
        public int ReconnectDelaySeconds { get; set; } = 5;
        public int MaxReconnectAttempts { get; set; } = 10;
    }

    public sealed class OpcUaAdapterConfig
    {
        public bool Enabled { get; set; } = true;
        public string ServerUrl { get; set; } = "opc.tcp://localhost:4840";
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool AcceptUntrustedCertificates { get; set; } = false;
        public int SessionTimeoutMs { get; set; } = 30000;
        public int PublishingIntervalMs { get; set; } = 1000;
        public List<OpcUaNodeConfig> NodesToMonitor { get; set; } = new();
    }

    public sealed class OpcUaNodeConfig
    {
        public string NodeId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SamplingIntervalMs { get; set; } = 500;
        public double? MinThreshold { get; set; }
        public double? MaxThreshold { get; set; }
        public string Unit { get; set; } = string.Empty;
    }

    public sealed class ModbusAdapterConfig
    {
        public bool Enabled { get; set; } = true;
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 502;
        public byte SlaveId { get; set; } = 1;
        public int PollingIntervalMs { get; set; } = 1000;
        public int TimeoutMs { get; set; } = 3000;
        public int MaxRetries { get; set; } = 3;
        public List<ModbusRegisterConfig> Registers { get; set; } = new();
    }

    public sealed class ModbusRegisterConfig
    {
        public ushort Address { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "holding"; // holding, input, coil, discrete
        public string DataType { get; set; } = "uint16"; // uint16, int16, float32, bool
        public double Scale { get; set; } = 1.0;
        public double Offset { get; set; } = 0.0;
        public string Unit { get; set; } = string.Empty;
        public double? MinThreshold { get; set; }
        public double? MaxThreshold { get; set; }
    }

    public sealed class IoTHubConfig
    {
        public string ConnectionString { get; set; } = string.Empty;
        public int TelemetrySendIntervalMs { get; set; } = 5000;
        public int MaxBatchSize { get; set; } = 100;
        public int RetryMaxAttempts { get; set; } = 5;
        public int RetryBaseDelayMs { get; set; } = 1000;
        public string ModelId { get; set; } = string.Empty;
    }

    public sealed class AlertConfig
    {
        public int MaxActiveAlerts { get; set; } = 1000;
        public bool LogAlertsLocally { get; set; } = true;
        public bool SendAlertsToCloud { get; set; } = true;
        public int AlertCooldownSeconds { get; set; } = 60;
    }
}
