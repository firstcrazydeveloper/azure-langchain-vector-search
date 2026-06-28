using System;
using System.Collections.Generic;
using System.Text.Json;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace IoTEdgeGateway.Application.Translators
{
    public sealed class MqttDataTranslator : IDataTranslator
    {
        private readonly ILogger<MqttDataTranslator> _logger;

        public ProtocolType SupportedProtocol => ProtocolType.Mqtt;

        public MqttDataTranslator(ILogger<MqttDataTranslator> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IEnumerable<DataPoint> Translate(DeviceMessage message)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));

            var dataPoints = new List<DataPoint>();

            if (string.IsNullOrWhiteSpace(message.RawPayload))
            {
                _logger.LogWarning("MQTT message from {DeviceId} has empty payload.", message.DeviceId);
                return dataPoints;
            }

            try
            {
                using var doc = JsonDocument.Parse(message.RawPayload);
                ParseJsonElement(doc.RootElement, dataPoints, string.Empty);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse MQTT JSON payload from {DeviceId}.", message.DeviceId);
                // Treat raw payload as a single string data point rather than dropping the message
                dataPoints.Add(new DataPoint("raw_payload", message.RawPayload, DataPointType.String,
                    timestamp: message.ReceivedAt, isValid: false));
            }

            return dataPoints;
        }

        private static void ParseJsonElement(JsonElement element, List<DataPoint> results, string prefix)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                        ParseJsonElement(property.Value, results, key);
                    }
                    break;

                case JsonValueKind.Number:
                    if (element.TryGetDouble(out var numVal))
                    {
                        var type = InferDataPointType(prefix);
                        var unit = InferUnit(prefix);
                        results.Add(new DataPoint(prefix, numVal, type, unit));
                    }
                    break;

                case JsonValueKind.String:
                    results.Add(new DataPoint(prefix, element.GetString(), DataPointType.String));
                    break;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    results.Add(new DataPoint(prefix, element.GetBoolean(), DataPointType.Boolean));
                    break;

                case JsonValueKind.Array:
                    var idx = 0;
                    foreach (var item in element.EnumerateArray())
                        ParseJsonElement(item, results, $"{prefix}[{idx++}]");
                    break;
            }
        }

        private static DataPointType InferDataPointType(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("temp")) return DataPointType.Temperature;
            if (lower.Contains("pressure") || lower.Contains("press")) return DataPointType.Pressure;
            if (lower.Contains("humid")) return DataPointType.Humidity;
            if (lower.Contains("vibr")) return DataPointType.Vibration;
            if (lower.Contains("current") || lower.Contains("amp")) return DataPointType.Current;
            if (lower.Contains("voltage") || lower.Contains("volt")) return DataPointType.Voltage;
            if (lower.Contains("flow")) return DataPointType.FlowRate;
            if (lower.Contains("speed") || lower.Contains("rpm")) return DataPointType.Speed;
            if (lower.Contains("pos") || lower.Contains("angle")) return DataPointType.Position;
            return DataPointType.Unknown;
        }

        private static string InferUnit(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("temp")) return "°C";
            if (lower.Contains("pressure")) return "bar";
            if (lower.Contains("humid")) return "%";
            if (lower.Contains("current")) return "A";
            if (lower.Contains("voltage")) return "V";
            if (lower.Contains("flow")) return "L/min";
            if (lower.Contains("speed") || lower.Contains("rpm")) return "RPM";
            return string.Empty;
        }
    }
}
