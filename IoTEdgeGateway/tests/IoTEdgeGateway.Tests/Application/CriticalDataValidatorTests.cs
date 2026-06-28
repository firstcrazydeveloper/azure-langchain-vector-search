using System;
using System.Linq;
using FluentAssertions;
using IoTEdgeGateway.Application.Validators;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IoTEdgeGateway.Tests.Application
{
    public class CriticalDataValidatorTests
    {
        private static CriticalDataValidator CreateValidator(Action<ValidationOptions>? configure = null)
        {
            var opts = new ValidationOptions();
            configure?.Invoke(opts);
            return new CriticalDataValidator(
                Options.Create(opts),
                NullLogger<CriticalDataValidator>.Instance);
        }

        [Fact]
        public void Validate_TemperatureInRange_IsValid()
        {
            var validator = CreateValidator();
            var dp = new DataPoint("temp", 50.0, DataPointType.Temperature);

            var result = validator.Validate(dp);
            result.IsValid.Should().BeTrue();
        }

        [Fact]
        public void Validate_TemperatureTooHigh_IsInvalid()
        {
            var validator = CreateValidator(o => o.MaxTemperatureCelsius = 100.0);
            var dp = new DataPoint("temp", 200.0, DataPointType.Temperature);

            var result = validator.Validate(dp);
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("Temperature"));
        }

        [Fact]
        public void Validate_TemperatureTooLow_IsInvalid()
        {
            var validator = CreateValidator(o => o.MinTemperatureCelsius = -10.0);
            var dp = new DataPoint("temp", -50.0, DataPointType.Temperature);

            var result = validator.Validate(dp);
            result.IsValid.Should().BeFalse();
        }

        [Fact]
        public void Validate_HumidityOver100_IsInvalid()
        {
            var validator = CreateValidator();
            var dp = new DataPoint("hum", 105.0, DataPointType.Humidity, "%");

            var result = validator.Validate(dp);
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("Humidity"));
        }

        [Fact]
        public void Validate_NegativePressure_IsInvalid()
        {
            var validator = CreateValidator();
            var dp = new DataPoint("press", -5.0, DataPointType.Pressure, "bar");

            var result = validator.Validate(dp);
            result.IsValid.Should().BeFalse();
        }

        [Fact]
        public void Validate_StaleTimestamp_IsInvalid()
        {
            var validator = CreateValidator(o => o.MaxDataAge = TimeSpan.FromMinutes(1));
            var dp = new DataPoint("temp", 50.0, DataPointType.Temperature,
                timestamp: DateTimeOffset.UtcNow.AddMinutes(-10));

            var result = validator.Validate(dp);
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("stale"));
        }

        [Fact]
        public void Validate_FutureTimestamp_IsInvalid()
        {
            var validator = CreateValidator();
            var dp = new DataPoint("temp", 50.0, DataPointType.Temperature,
                timestamp: DateTimeOffset.UtcNow.AddMinutes(5));

            var result = validator.Validate(dp);
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("future"));
        }

        [Fact]
        public void Validate_ExceedsCustomThreshold_IsInvalid()
        {
            var validator = CreateValidator();
            var dp = new DataPoint("temp", 150.0, DataPointType.Temperature,
                minThreshold: 0, maxThreshold: 100);

            var result = validator.Validate(dp);
            result.IsValid.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("maximum threshold"));
        }

        [Fact]
        public void Validate_BooleanDataPoint_AlwaysValid()
        {
            var validator = CreateValidator();
            var dp = new DataPoint("active", true, DataPointType.Boolean);

            var result = validator.Validate(dp);
            result.IsValid.Should().BeTrue();
        }

        [Fact]
        public void Validate_NullDataPoint_Throws()
        {
            var validator = CreateValidator();
            Action act = () => validator.Validate(null!);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void ValidateAll_MixedPoints_ReturnsOneResultPerPoint()
        {
            var validator = CreateValidator();
            var points = new[]
            {
                new DataPoint("temp", 50.0, DataPointType.Temperature),
                new DataPoint("hum", 60.0, DataPointType.Humidity),
                new DataPoint("press", -1.0, DataPointType.Pressure)
            };

            var results = validator.ValidateAll(points).ToList();
            results.Should().HaveCount(3);
            results[0].IsValid.Should().BeTrue();
            results[1].IsValid.Should().BeTrue();
            results[2].IsValid.Should().BeFalse();
        }

        [Fact]
        public void ValidateAll_NullCollection_Throws()
        {
            var validator = CreateValidator();
            Action act = () => validator.ValidateAll(null!).ToList();
            act.Should().Throw<ArgumentNullException>();
        }

        [Theory]
        [InlineData(0.0, true)]
        [InlineData(50.0, true)]
        [InlineData(-1.0, false)]
        [InlineData(10001.0, false)]
        public void Validate_FlowRate_BoundaryConditions(double value, bool expectedValid)
        {
            var validator = CreateValidator(o => o.MaxFlowRateLpm = 10000.0);
            var dp = new DataPoint("flow", value, DataPointType.FlowRate);

            validator.Validate(dp).IsValid.Should().Be(expectedValid);
        }
    }
}
