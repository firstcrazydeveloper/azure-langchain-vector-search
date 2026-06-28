using System;
using System.Collections.Generic;
using System.Linq;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using IoTEdgeGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IoTEdgeGateway.Application.Validators
{
    public sealed class ValidationOptions
    {
        public double MaxTemperatureCelsius { get; set; } = 150.0;
        public double MinTemperatureCelsius { get; set; } = -40.0;
        public double MaxPressureBar { get; set; } = 250.0;
        public double MinPressureBar { get; set; } = 0.0;
        public double MaxHumidityPercent { get; set; } = 100.0;
        public double MinHumidityPercent { get; set; } = 0.0;
        public double MaxVibrationG { get; set; } = 50.0;
        public double MaxCurrentAmps { get; set; } = 1000.0;
        public double MaxVoltageVolts { get; set; } = 15000.0;
        public double MaxFlowRateLpm { get; set; } = 10000.0;
        public double MaxSpeedRpm { get; set; } = 50000.0;
        public TimeSpan MaxDataAge { get; set; } = TimeSpan.FromMinutes(5);
    }

    public sealed class CriticalDataValidator : IDataValidator
    {
        private readonly ValidationOptions _options;
        private readonly ILogger<CriticalDataValidator> _logger;

        public CriticalDataValidator(IOptions<ValidationOptions> options,
            ILogger<CriticalDataValidator> logger)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ValidationResult Validate(DataPoint dataPoint)
        {
            if (dataPoint is null) throw new ArgumentNullException(nameof(dataPoint));

            var errors = new List<string>();

            ValidateTimestamp(dataPoint, errors);

            var numericValue = dataPoint.AsDouble();
            if (numericValue.HasValue)
            {
                ValidateNumericRange(dataPoint, numericValue.Value, errors);
                ValidatePhysicsConstraints(dataPoint, numericValue.Value, errors);
            }
            else if (dataPoint.Type != DataPointType.Boolean && dataPoint.Type != DataPointType.String)
            {
                errors.Add($"DataPoint '{dataPoint.Name}' expected numeric value but got: {dataPoint.Value?.GetType().Name ?? "null"}");
            }

            if (errors.Count > 0)
                _logger.LogWarning("Validation failures for '{Name}': {Errors}",
                    dataPoint.Name, string.Join("; ", errors));

            return errors.Count == 0
                ? ValidationResult.Success()
                : ValidationResult.Failure(errors);
        }

        public IEnumerable<ValidationResult> ValidateAll(IEnumerable<DataPoint> dataPoints)
        {
            if (dataPoints is null) throw new ArgumentNullException(nameof(dataPoints));
            return dataPoints.Select(Validate);
        }

        private void ValidateTimestamp(DataPoint dp, List<string> errors)
        {
            var age = DateTimeOffset.UtcNow - dp.Timestamp;
            if (age > _options.MaxDataAge)
                errors.Add($"DataPoint '{dp.Name}' is stale: age={age.TotalMinutes:F1}min exceeds max={_options.MaxDataAge.TotalMinutes}min.");

            if (dp.Timestamp > DateTimeOffset.UtcNow.AddSeconds(30))
                errors.Add($"DataPoint '{dp.Name}' has a future timestamp: {dp.Timestamp:O}.");
        }

        private void ValidateNumericRange(DataPoint dp, double value, List<string> errors)
        {
            if (dp.MinThreshold.HasValue && value < dp.MinThreshold.Value)
                errors.Add($"'{dp.Name}'={value} is below minimum threshold {dp.MinThreshold.Value}.");

            if (dp.MaxThreshold.HasValue && value > dp.MaxThreshold.Value)
                errors.Add($"'{dp.Name}'={value} exceeds maximum threshold {dp.MaxThreshold.Value}.");
        }

        private void ValidatePhysicsConstraints(DataPoint dp, double value, List<string> errors)
        {
            switch (dp.Type)
            {
                case DataPointType.Temperature:
                    if (value < _options.MinTemperatureCelsius || value > _options.MaxTemperatureCelsius)
                        errors.Add($"Temperature {value}°C outside physics range [{_options.MinTemperatureCelsius}, {_options.MaxTemperatureCelsius}].");
                    break;

                case DataPointType.Pressure:
                    if (value < _options.MinPressureBar || value > _options.MaxPressureBar)
                        errors.Add($"Pressure {value} bar outside physics range [{_options.MinPressureBar}, {_options.MaxPressureBar}].");
                    break;

                case DataPointType.Humidity:
                    if (value < _options.MinHumidityPercent || value > _options.MaxHumidityPercent)
                        errors.Add($"Humidity {value}% outside valid range [0, 100].");
                    break;

                case DataPointType.Vibration:
                    if (value < 0 || value > _options.MaxVibrationG)
                        errors.Add($"Vibration {value}g outside valid range [0, {_options.MaxVibrationG}].");
                    break;

                case DataPointType.Current:
                    if (value < 0 || value > _options.MaxCurrentAmps)
                        errors.Add($"Current {value}A outside valid range [0, {_options.MaxCurrentAmps}].");
                    break;

                case DataPointType.Voltage:
                    if (value < 0 || value > _options.MaxVoltageVolts)
                        errors.Add($"Voltage {value}V outside valid range [0, {_options.MaxVoltageVolts}].");
                    break;

                case DataPointType.FlowRate:
                    if (value < 0 || value > _options.MaxFlowRateLpm)
                        errors.Add($"FlowRate {value} L/min outside valid range [0, {_options.MaxFlowRateLpm}].");
                    break;

                case DataPointType.Speed:
                    if (value < 0 || value > _options.MaxSpeedRpm)
                        errors.Add($"Speed {value} RPM outside valid range [0, {_options.MaxSpeedRpm}].");
                    break;
            }
        }
    }
}
