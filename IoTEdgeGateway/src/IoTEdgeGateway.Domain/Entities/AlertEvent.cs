using System;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.ValueObjects;

namespace IoTEdgeGateway.Domain.Entities
{
    public sealed class AlertEvent
    {
        public Guid AlertId { get; }
        public DeviceId DeviceId { get; }
        public AlertSeverity Severity { get; }
        public string Message { get; }
        public string DataPointName { get; }
        public object? ActualValue { get; }
        public double? ThresholdValue { get; }
        public DateTimeOffset OccurredAt { get; }
        public bool IsAcknowledged { get; private set; }
        public DateTimeOffset? AcknowledgedAt { get; private set; }

        public AlertEvent(
            DeviceId deviceId,
            AlertSeverity severity,
            string message,
            string dataPointName,
            object? actualValue = null,
            double? thresholdValue = null)
        {
            AlertId = Guid.NewGuid();
            DeviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            Severity = severity;
            Message = string.IsNullOrWhiteSpace(message)
                ? throw new ArgumentException("Alert message cannot be empty.", nameof(message))
                : message;
            DataPointName = dataPointName ?? string.Empty;
            ActualValue = actualValue;
            ThresholdValue = thresholdValue;
            OccurredAt = DateTimeOffset.UtcNow;
            IsAcknowledged = false;
        }

        public void Acknowledge()
        {
            IsAcknowledged = true;
            AcknowledgedAt = DateTimeOffset.UtcNow;
        }
    }
}
