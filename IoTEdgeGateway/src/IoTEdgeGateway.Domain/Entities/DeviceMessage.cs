using System;
using System.Collections.Generic;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.ValueObjects;

namespace IoTEdgeGateway.Domain.Entities
{
    public sealed class DeviceMessage
    {
        public Guid MessageId { get; }
        public DeviceId DeviceId { get; }
        public ProtocolType Protocol { get; }
        public IReadOnlyList<DataPoint> DataPoints { get; }
        public DateTimeOffset ReceivedAt { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }
        public string RawPayload { get; }

        public DeviceMessage(
            DeviceId deviceId,
            ProtocolType protocol,
            IEnumerable<DataPoint> dataPoints,
            string rawPayload = "",
            Dictionary<string, string>? metadata = null,
            DateTimeOffset? receivedAt = null)
        {
            MessageId = Guid.NewGuid();
            DeviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            Protocol = protocol;
            DataPoints = new List<DataPoint>(dataPoints ?? throw new ArgumentNullException(nameof(dataPoints)));
            RawPayload = rawPayload ?? string.Empty;
            Metadata = metadata ?? new Dictionary<string, string>();
            ReceivedAt = receivedAt ?? DateTimeOffset.UtcNow;
        }
    }
}
