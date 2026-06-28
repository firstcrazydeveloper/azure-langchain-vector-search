using System;
using FluentAssertions;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using Xunit;

namespace IoTEdgeGateway.Tests.Domain
{
    public class DataPointTests
    {
        [Fact]
        public void Constructor_ValidArguments_CreatesDataPoint()
        {
            var dp = new DataPoint("temperature", 75.5, DataPointType.Temperature, "°C",
                minThreshold: 0, maxThreshold: 100);

            dp.Name.Should().Be("temperature");
            dp.Value.Should().Be(75.5);
            dp.Type.Should().Be(DataPointType.Temperature);
            dp.Unit.Should().Be("°C");
            dp.IsValid.Should().BeTrue();
            dp.MinThreshold.Should().Be(0);
            dp.MaxThreshold.Should().Be(100);
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(null)]
        public void Constructor_EmptyName_ThrowsArgumentException(string? name)
        {
            Action act = () => new DataPoint(name!, 1.0, DataPointType.Temperature);
            act.Should().Throw<ArgumentException>().WithParameterName("name");
        }

        [Fact]
        public void AsDouble_NumericTypes_ReturnsCorrectDouble()
        {
            new DataPoint("a", 3.14, DataPointType.Temperature).AsDouble().Should().Be(3.14);
            new DataPoint("b", 42, DataPointType.Temperature).AsDouble().Should().Be(42.0);
            new DataPoint("c", 100L, DataPointType.Temperature).AsDouble().Should().Be(100.0);
            new DataPoint("d", 1.5f, DataPointType.Temperature).AsDouble().Should().BeApproximately(1.5, 0.001);
        }

        [Fact]
        public void AsDouble_StringNumeric_ParsesSuccessfully()
        {
            new DataPoint("e", "99.9", DataPointType.Temperature).AsDouble().Should().Be(99.9);
        }

        [Fact]
        public void AsDouble_NonNumericString_ReturnsNull()
        {
            new DataPoint("f", "not-a-number", DataPointType.String).AsDouble().Should().BeNull();
        }

        [Fact]
        public void AsDouble_NullValue_ReturnsNull()
        {
            new DataPoint("g", null, DataPointType.Unknown).AsDouble().Should().BeNull();
        }

        [Theory]
        [InlineData(150.0, 0, 100, true)]    // Above max
        [InlineData(-5.0, 0, 100, true)]     // Below min
        [InlineData(50.0, 0, 100, false)]    // Within range
        [InlineData(100.0, 0, 100, false)]   // At max boundary — not exceeded
        [InlineData(0.0, 0, 100, false)]     // At min boundary — not exceeded
        public void ExceedsThreshold_ReturnsCorrectResult(double value, double min, double max, bool expected)
        {
            var dp = new DataPoint("temp", value, DataPointType.Temperature,
                minThreshold: min, maxThreshold: max);

            dp.ExceedsThreshold().Should().Be(expected);
        }

        [Fact]
        public void ExceedsThreshold_NoThresholds_AlwaysReturnsFalse()
        {
            new DataPoint("x", 9999.0, DataPointType.Temperature).ExceedsThreshold().Should().BeFalse();
        }

        [Fact]
        public void Timestamp_WhenNotProvided_DefaultsToUtcNow()
        {
            var before = DateTimeOffset.UtcNow;
            var dp = new DataPoint("t", 1.0, DataPointType.Temperature);
            var after = DateTimeOffset.UtcNow;

            dp.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        }

        [Fact]
        public void IsNumeric_ReturnsTrueForNumericValues()
        {
            new DataPoint("a", 1.0, DataPointType.Temperature).IsNumeric().Should().BeTrue();
            new DataPoint("b", 1, DataPointType.Temperature).IsNumeric().Should().BeTrue();
            new DataPoint("c", 1L, DataPointType.Temperature).IsNumeric().Should().BeTrue();
        }

        [Fact]
        public void IsNumeric_ReturnsFalseForStringValue()
        {
            new DataPoint("a", "hello", DataPointType.String).IsNumeric().Should().BeFalse();
        }
    }
}
