using System;
using System.Collections.Generic;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace IoTEdgeGateway.Application.Translators
{
    /// <summary>
    /// Translates raw Modbus register values using a register map from device metadata.
    /// Each DataPoint name is "register_{address}" unless overridden in metadata.
    /// </summary>
    public sealed class ModbusDataTranslator : IDataTranslator
    {
        private readonly ILogger<ModbusDataTranslator> _logger;

        public ProtocolType SupportedProtocol => ProtocolType.ModbusTcp;

        public ModbusDataTranslator(ILogger<ModbusDataTranslator> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IEnumerable<DataPoint> Translate(DeviceMessage message)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));

            var results = new List<DataPoint>();

            foreach (var dp in message.DataPoints)
            {
                var translated = ApplyRegisterMap(dp, message.Metadata);
                if (translated is not null)
                    results.Add(translated);
            }

            return results;
        }

        private DataPoint? ApplyRegisterMap(DataPoint source,
            IReadOnlyDictionary<string, string> metadata)
        {
            var registerKey = source.Name; // e.g., "register_40001"

            // Resolve human-readable name from register map
            var name = source.Name;
            if (metadata.TryGetValue($"register.{registerKey}.name", out var mappedName)
                && !string.IsNullOrWhiteSpace(mappedName))
            {
                name = mappedName;
            }

            // Apply scaling factor if defined (e.g., raw integer / 10.0 = actual temp)
            var value = source.Value;
            if (source.Value is double rawDouble
                && metadata.TryGetValue($"register.{registerKey}.scale", out var scaleStr)
                && double.TryParse(scaleStr, out var scale)
                && scale != 0)
            {
                value = rawDouble * scale;
            }

            // Apply offset
            if (value is double scaledValue
                && metadata.TryGetValue($"register.{registerKey}.offset", out var offsetStr)
                && double.TryParse(offsetStr, out var offset))
            {
                value = scaledValue + offset;
            }

            var type = ResolveType(metadata, registerKey, source.Type);
            var unit = metadata.TryGetValue($"register.{registerKey}.unit", out var u) ? u : source.Unit;

            double? minThreshold = null;
            double? maxThreshold = null;

            if (metadata.TryGetValue($"register.{registerKey}.min", out var minStr)
                && double.TryParse(minStr, out var min))
                minThreshold = min;

            if (metadata.TryGetValue($"register.{registerKey}.max", out var maxStr)
                && double.TryParse(maxStr, out var max))
                maxThreshold = max;

            return new DataPoint(name, value, type, unit ?? string.Empty,
                source.Timestamp, source.IsValid, minThreshold, maxThreshold);
        }

        private static DataPointType ResolveType(IReadOnlyDictionary<string, string> metadata,
            string registerKey, DataPointType fallback)
        {
            if (!metadata.TryGetValue($"register.{registerKey}.type", out var typeStr))
                return fallback;

            return typeStr?.ToLowerInvariant() switch
            {
                "temperature" => DataPointType.Temperature,
                "pressure" => DataPointType.Pressure,
                "humidity" => DataPointType.Humidity,
                "vibration" => DataPointType.Vibration,
                "current" => DataPointType.Current,
                "voltage" => DataPointType.Voltage,
                "flowrate" => DataPointType.FlowRate,
                "speed" => DataPointType.Speed,
                "position" => DataPointType.Position,
                "boolean" => DataPointType.Boolean,
                _ => fallback
            };
        }
    }
}
