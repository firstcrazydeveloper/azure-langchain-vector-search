using System;
using System.Collections.Generic;
using System.Linq;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IoTEdgeGateway.Application.Filters
{
    public sealed class FilterOptions
    {
        public HashSet<DataPointType> AllowedTypes { get; set; } = new()
        {
            DataPointType.Temperature, DataPointType.Pressure, DataPointType.Humidity,
            DataPointType.Vibration, DataPointType.Current, DataPointType.Voltage,
            DataPointType.FlowRate, DataPointType.Speed, DataPointType.Position
        };

        public HashSet<string> BlockedDataPointNames { get; set; } = new();
        public bool ExcludeInvalidDataPoints { get; set; } = true;
        public bool EnableDeduplication { get; set; } = true;
        public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromSeconds(5);
        public int MaxDataPointsPerMessage { get; set; } = 200;
    }

    public sealed class DataPointFilter : IDataFilter
    {
        private readonly FilterOptions _options;
        private readonly ILogger<DataPointFilter> _logger;

        // Deduplication cache: deviceId:dataPointName -> (value, timestamp)
        private readonly Dictionary<string, (object? Value, DateTimeOffset Seen)> _dedupeCache = new();

        public DataPointFilter(IOptions<FilterOptions> options, ILogger<DataPointFilter> logger)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IEnumerable<DataPoint> Filter(IEnumerable<DataPoint> dataPoints, string deviceId)
        {
            if (dataPoints is null) throw new ArgumentNullException(nameof(dataPoints));
            if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device ID required.", nameof(deviceId));

            var results = new List<DataPoint>();
            var now = DateTimeOffset.UtcNow;

            foreach (var dp in dataPoints.Take(_options.MaxDataPointsPerMessage))
            {
                if (_options.ExcludeInvalidDataPoints && !dp.IsValid)
                {
                    _logger.LogDebug("Filtered invalid data point '{Name}' from {DeviceId}.", dp.Name, deviceId);
                    continue;
                }

                if (!_options.AllowedTypes.Contains(dp.Type) && dp.Type != DataPointType.Unknown)
                {
                    _logger.LogDebug("Filtered data point type {Type} for '{Name}'.", dp.Type, dp.Name);
                    continue;
                }

                if (_options.BlockedDataPointNames.Contains(dp.Name))
                {
                    _logger.LogDebug("Filtered blocked data point '{Name}'.", dp.Name);
                    continue;
                }

                if (_options.EnableDeduplication && IsDuplicate(dp, deviceId, now))
                {
                    _logger.LogDebug("Deduplicated data point '{Name}' from {DeviceId}.", dp.Name, deviceId);
                    continue;
                }

                results.Add(dp);
            }

            PurgeExpiredDedupeEntries(now);
            return results;
        }

        private bool IsDuplicate(DataPoint dp, string deviceId, DateTimeOffset now)
        {
            var cacheKey = $"{deviceId}:{dp.Name}";

            if (_dedupeCache.TryGetValue(cacheKey, out var cached))
            {
                if (now - cached.Seen < _options.DeduplicationWindow
                    && Equals(cached.Value, dp.Value))
                {
                    return true;
                }
            }

            _dedupeCache[cacheKey] = (dp.Value, now);
            return false;
        }

        private void PurgeExpiredDedupeEntries(DateTimeOffset now)
        {
            var expiredKeys = _dedupeCache
                .Where(kvp => now - kvp.Value.Seen > _options.DeduplicationWindow * 2)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
                _dedupeCache.Remove(key);
        }
    }
}
