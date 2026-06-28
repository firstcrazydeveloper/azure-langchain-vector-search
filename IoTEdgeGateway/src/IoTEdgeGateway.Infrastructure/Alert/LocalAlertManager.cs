using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using IoTEdgeGateway.Domain.ValueObjects;
using IoTEdgeGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IoTEdgeGateway.Infrastructure.Alert
{
    public sealed class LocalAlertManager : IAlertService
    {
        private readonly AlertConfig _config;
        private readonly IIoTHubClient _iotHubClient;
        private readonly ILogger<LocalAlertManager> _logger;

        private readonly ConcurrentDictionary<Guid, AlertEvent> _activeAlerts = new();

        // Cooldown tracker: deviceId:dataPointName -> last alert time
        private readonly ConcurrentDictionary<string, DateTimeOffset> _alertCooldowns = new();

        public event EventHandler<AlertEvent>? AlertRaised;

        public LocalAlertManager(
            IOptions<GatewayConfiguration> config,
            IIoTHubClient iotHubClient,
            ILogger<LocalAlertManager> logger)
        {
            _config = config?.Value?.Alert ?? throw new ArgumentNullException(nameof(config));
            _iotHubClient = iotHubClient ?? throw new ArgumentNullException(nameof(iotHubClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task RaiseAlertAsync(AlertEvent alert, CancellationToken cancellationToken = default)
        {
            if (alert is null) throw new ArgumentNullException(nameof(alert));

            var cooldownKey = $"{alert.DeviceId}:{alert.DataPointName}";

            // Cooldown check — don't spam the same alert repeatedly
            if (_alertCooldowns.TryGetValue(cooldownKey, out var lastRaised))
            {
                var cooldown = TimeSpan.FromSeconds(_config.AlertCooldownSeconds);
                if (DateTimeOffset.UtcNow - lastRaised < cooldown)
                {
                    _logger.LogDebug("Alert for '{DataPoint}' on {Device} suppressed (cooldown).",
                        alert.DataPointName, alert.DeviceId);
                    return;
                }
            }

            // Capacity check
            if (_activeAlerts.Count >= _config.MaxActiveAlerts)
            {
                _logger.LogWarning("Max active alerts ({Max}) reached. Dropping oldest.", _config.MaxActiveAlerts);
                PurgeOldestAlert();
            }

            _activeAlerts[alert.AlertId] = alert;
            _alertCooldowns[cooldownKey] = DateTimeOffset.UtcNow;

            if (_config.LogAlertsLocally)
            {
                var logLevel = alert.Severity switch
                {
                    AlertSeverity.Emergency or AlertSeverity.Critical => LogLevel.Critical,
                    AlertSeverity.Warning => LogLevel.Warning,
                    _ => LogLevel.Information
                };

                _logger.Log(logLevel, "[ALERT {Severity}] Device={DeviceId} | {Message}",
                    alert.Severity, alert.DeviceId, alert.Message);
            }

            AlertRaised?.Invoke(this, alert);

            if (_config.SendAlertsToCloud)
            {
                try
                {
                    await _iotHubClient.SendAlertAsync(alert, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send alert {AlertId} to cloud.", alert.AlertId);
                }
            }
        }

        public Task AcknowledgeAlertAsync(Guid alertId, CancellationToken cancellationToken = default)
        {
            if (_activeAlerts.TryGetValue(alertId, out var alert))
            {
                alert.Acknowledge();
                _logger.LogInformation("Alert {AlertId} acknowledged.", alertId);
            }
            else
            {
                _logger.LogWarning("Acknowledge requested for unknown alert {AlertId}.", alertId);
            }

            return Task.CompletedTask;
        }

        public Task<IEnumerable<AlertEvent>> GetActiveAlertsAsync(CancellationToken cancellationToken = default)
        {
            var alerts = _activeAlerts.Values
                .Where(a => !a.IsAcknowledged)
                .OrderByDescending(a => a.Severity)
                .ThenByDescending(a => a.OccurredAt)
                .AsEnumerable();

            return Task.FromResult(alerts);
        }

        public Task<IEnumerable<AlertEvent>> GetAlertsByDeviceAsync(DeviceId deviceId,
            CancellationToken cancellationToken = default)
        {
            if (deviceId is null) throw new ArgumentNullException(nameof(deviceId));

            var alerts = _activeAlerts.Values
                .Where(a => a.DeviceId.Value == deviceId.Value)
                .OrderByDescending(a => a.OccurredAt)
                .AsEnumerable();

            return Task.FromResult(alerts);
        }

        public async Task EvaluateDataPointsAsync(DeviceId deviceId,
            IEnumerable<DataPoint> dataPoints, CancellationToken cancellationToken = default)
        {
            if (deviceId is null) throw new ArgumentNullException(nameof(deviceId));
            if (dataPoints is null) throw new ArgumentNullException(nameof(dataPoints));

            var points = dataPoints.ToList();

            // Cross-point rule: high temp + high vibration = potential machine fault
            var temp = points.FirstOrDefault(d => d.Type == DataPointType.Temperature)?.AsDouble();
            var vib = points.FirstOrDefault(d => d.Type == DataPointType.Vibration)?.AsDouble();

            if (temp > 100 && vib > 5.0)
            {
                var alert = new AlertEvent(
                    deviceId,
                    AlertSeverity.Critical,
                    $"Combined fault: Temperature={temp}°C AND Vibration={vib}g. Possible bearing failure.",
                    "cross-point-temp-vib",
                    new { temperature = temp, vibration = vib });

                await RaiseAlertAsync(alert, cancellationToken);
            }

            // Cross-point rule: high current + low speed = possible motor stall
            var current = points.FirstOrDefault(d => d.Type == DataPointType.Current)?.AsDouble();
            var speed = points.FirstOrDefault(d => d.Type == DataPointType.Speed)?.AsDouble();

            if (current > 50 && speed < 10)
            {
                var alert = new AlertEvent(
                    deviceId,
                    AlertSeverity.Emergency,
                    $"Possible motor stall: Current={current}A but Speed={speed}RPM.",
                    "cross-point-current-speed",
                    new { current, speed });

                await RaiseAlertAsync(alert, cancellationToken);
            }

            // Evaluate individual threshold exceedances
            foreach (var dp in points.Where(p => p.ExceedsThreshold()))
            {
                var severity = dp.Type switch
                {
                    DataPointType.Temperature or DataPointType.Pressure => AlertSeverity.Critical,
                    DataPointType.Voltage or DataPointType.Current => AlertSeverity.Warning,
                    _ => AlertSeverity.Warning
                };

                var alert = new AlertEvent(
                    deviceId,
                    severity,
                    $"Threshold exceeded: {dp.Name}={dp.AsDouble()} {dp.Unit}",
                    dp.Name,
                    dp.Value,
                    dp.AsDouble() > dp.MaxThreshold ? dp.MaxThreshold : dp.MinThreshold);

                await RaiseAlertAsync(alert, cancellationToken);
            }
        }

        private void PurgeOldestAlert()
        {
            var oldest = _activeAlerts.Values
                .OrderBy(a => a.OccurredAt)
                .FirstOrDefault();

            if (oldest is not null)
                _activeAlerts.TryRemove(oldest.AlertId, out _);
        }
    }
}
