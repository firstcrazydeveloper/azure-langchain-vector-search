using System;
using System.Collections.Generic;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace IoTEdgeGateway.Application.Translators
{
    public sealed class OpcUaDataTranslator : IDataTranslator
    {
        private readonly ILogger<OpcUaDataTranslator> _logger;

        public ProtocolType SupportedProtocol => ProtocolType.OpcUa;

        public OpcUaDataTranslator(ILogger<OpcUaDataTranslator> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IEnumerable<DataPoint> Translate(DeviceMessage message)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));

            var dataPoints = new List<DataPoint>();

            // OPC UA data arrives via IProtocolAdapter events with DataPoints already partially
            // structured. The metadata carries OPC UA node information.
            foreach (var dp in message.DataPoints)
            {
                var translated = TranslateOpcUaDataPoint(dp, message.Metadata);
                dataPoints.Add(translated);
            }

            return dataPoints;
        }

        private DataPoint TranslateOpcUaDataPoint(DataPoint source,
            IReadOnlyDictionary<string, string> metadata)
        {
            // Map OPC UA node IDs to human-readable names if mapping exists
            var name = source.Name;
            if (metadata.TryGetValue($"node.{source.Name}.displayName", out var displayName)
                && !string.IsNullOrWhiteSpace(displayName))
            {
                name = displayName;
            }

            var type = MapOpcUaTypeToDataPointType(metadata, source.Name, source.Type);
            var unit = GetUnit(metadata, source.Name, source.Unit);

            // OPC UA quality check — Bad quality codes indicate invalid data
            var isValid = source.IsValid;
            if (metadata.TryGetValue($"node.{source.Name}.qualityCode", out var qualityStr)
                && uint.TryParse(qualityStr, out var qualityCode))
            {
                isValid = qualityCode == 0; // 0 = Good in OPC UA
            }

            return new DataPoint(name, source.Value, type, unit,
                source.Timestamp, isValid, source.MinThreshold, source.MaxThreshold);
        }

        private static DataPointType MapOpcUaTypeToDataPointType(
            IReadOnlyDictionary<string, string> metadata, string nodeName, DataPointType fallback)
        {
            if (metadata.TryGetValue($"node.{nodeName}.engineeringUnit", out var eu))
            {
                return eu?.ToLowerInvariant() switch
                {
                    "°c" or "celsius" or "fahrenheit" => DataPointType.Temperature,
                    "bar" or "psi" or "pa" or "kpa" => DataPointType.Pressure,
                    "%" or "rh" => DataPointType.Humidity,
                    "g" or "m/s²" => DataPointType.Vibration,
                    "a" or "ma" or "amp" => DataPointType.Current,
                    "v" or "mv" or "kv" => DataPointType.Voltage,
                    "l/min" or "m³/h" => DataPointType.FlowRate,
                    "rpm" or "rad/s" => DataPointType.Speed,
                    "mm" or "m" or "°" => DataPointType.Position,
                    _ => fallback
                };
            }
            return fallback;
        }

        private static string GetUnit(IReadOnlyDictionary<string, string> metadata,
            string nodeName, string fallback)
        {
            return metadata.TryGetValue($"node.{nodeName}.engineeringUnit", out var unit)
                ? unit ?? fallback
                : fallback;
        }
    }
}
