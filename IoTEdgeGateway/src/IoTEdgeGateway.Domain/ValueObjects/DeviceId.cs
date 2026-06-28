using System;

namespace IoTEdgeGateway.Domain.ValueObjects
{
    public sealed class DeviceId : IEquatable<DeviceId>
    {
        public string Value { get; }

        public DeviceId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Device ID cannot be null or empty.", nameof(value));
            if (value.Length > 128)
                throw new ArgumentException("Device ID cannot exceed 128 characters.", nameof(value));

            Value = value.Trim();
        }

        public bool Equals(DeviceId? other) => other is not null && Value == other.Value;
        public override bool Equals(object? obj) => obj is DeviceId id && Equals(id);
        public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
        public override string ToString() => Value;

        public static implicit operator string(DeviceId id) => id.Value;
        public static explicit operator DeviceId(string value) => new(value);
    }
}
