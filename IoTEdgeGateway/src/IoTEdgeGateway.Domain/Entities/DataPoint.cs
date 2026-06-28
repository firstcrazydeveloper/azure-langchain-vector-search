using System;
using IoTEdgeGateway.Domain.Enums;

namespace IoTEdgeGateway.Domain.Entities
{
    public sealed class DataPoint
    {
        public string Name { get; }
        public object? Value { get; }
        public DataPointType Type { get; }
        public string Unit { get; }
        public DateTimeOffset Timestamp { get; }
        public bool IsValid { get; }
        public double? MinThreshold { get; }
        public double? MaxThreshold { get; }

        public DataPoint(
            string name,
            object? value,
            DataPointType type,
            string unit = "",
            DateTimeOffset? timestamp = null,
            bool isValid = true,
            double? minThreshold = null,
            double? maxThreshold = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("DataPoint name cannot be empty.", nameof(name));

            Name = name;
            Value = value;
            Type = type;
            Unit = unit ?? string.Empty;
            Timestamp = timestamp ?? DateTimeOffset.UtcNow;
            IsValid = isValid;
            MinThreshold = minThreshold;
            MaxThreshold = maxThreshold;
        }

        public bool IsNumeric() =>
            Value is double or float or int or long or decimal;

        public double? AsDouble()
        {
            return Value switch
            {
                double d => d,
                float f => f,
                int i => i,
                long l => l,
                decimal dec => (double)dec,
                string s when double.TryParse(s, out var parsed) => parsed,
                _ => null
            };
        }

        public bool ExceedsThreshold()
        {
            var numericValue = AsDouble();
            if (numericValue is null) return false;
            if (MinThreshold.HasValue && numericValue < MinThreshold.Value) return true;
            if (MaxThreshold.HasValue && numericValue > MaxThreshold.Value) return true;
            return false;
        }
    }
}
