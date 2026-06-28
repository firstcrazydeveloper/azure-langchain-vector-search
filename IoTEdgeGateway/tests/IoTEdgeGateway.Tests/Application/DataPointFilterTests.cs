using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using IoTEdgeGateway.Application.Filters;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IoTEdgeGateway.Tests.Application
{
    public class DataPointFilterTests
    {
        private static DataPointFilter CreateFilter(Action<FilterOptions>? configure = null)
        {
            var opts = new FilterOptions();
            configure?.Invoke(opts);
            return new DataPointFilter(
                Options.Create(opts),
                NullLogger<DataPointFilter>.Instance);
        }

        private static DataPoint Temp(string name = "temperature", double value = 50.0, bool valid = true) =>
            new(name, value, DataPointType.Temperature, "°C", isValid: valid);

        [Fact]
        public void Filter_ValidDataPoints_PassThrough()
        {
            var filter = CreateFilter();
            var dps = new[] { Temp() };

            var result = filter.Filter(dps, "device-1").ToList();
            result.Should().HaveCount(1);
        }

        [Fact]
        public void Filter_InvalidDataPoint_ExcludedWhenOptionEnabled()
        {
            var filter = CreateFilter(o => o.ExcludeInvalidDataPoints = true);
            var dps = new[] { Temp(valid: false) };

            var result = filter.Filter(dps, "device-1").ToList();
            result.Should().BeEmpty();
        }

        [Fact]
        public void Filter_InvalidDataPoint_PassesWhenOptionDisabled()
        {
            var filter = CreateFilter(o => o.ExcludeInvalidDataPoints = false);
            var dps = new[] { Temp(valid: false) };

            var result = filter.Filter(dps, "device-1").ToList();
            result.Should().HaveCount(1);
        }

        [Fact]
        public void Filter_BlockedName_ExcludesDataPoint()
        {
            var filter = CreateFilter(o => o.BlockedDataPointNames.Add("raw_debug"));
            var dps = new[]
            {
                Temp("raw_debug"),
                Temp("temperature")
            };

            var result = filter.Filter(dps, "device-1").ToList();
            result.Should().HaveCount(1);
            result[0].Name.Should().Be("temperature");
        }

        [Fact]
        public void Filter_Deduplication_RemovesDuplicateWithinWindow()
        {
            var filter = CreateFilter(o =>
            {
                o.EnableDeduplication = true;
                o.DeduplicationWindow = TimeSpan.FromSeconds(60);
            });

            var dp = Temp(value: 50.0);
            var dps = new[] { dp, Temp(value: 50.0) };

            var result = filter.Filter(dps, "device-1").ToList();
            result.Should().HaveCount(1);
        }

        [Fact]
        public void Filter_Deduplication_AllowsDifferentValue()
        {
            var filter = CreateFilter(o => o.EnableDeduplication = true);

            // First value
            filter.Filter(new[] { Temp(value: 50.0) }, "device-1").ToList();

            // Different value — should NOT be deduplicated
            var result = filter.Filter(new[] { Temp(value: 75.0) }, "device-1").ToList();
            result.Should().HaveCount(1);
        }

        [Fact]
        public void Filter_Deduplication_Disabled_PassesDuplicates()
        {
            var filter = CreateFilter(o => o.EnableDeduplication = false);
            var dps = new[] { Temp(value: 50.0), Temp(value: 50.0) };

            var result = filter.Filter(dps, "device-1").ToList();
            result.Should().HaveCount(2);
        }

        [Fact]
        public void Filter_MaxDataPoints_LimitsCount()
        {
            var filter = CreateFilter(o => o.MaxDataPointsPerMessage = 3);
            var dps = Enumerable.Range(0, 10)
                .Select(i => Temp($"dp_{i}"))
                .ToArray();

            var result = filter.Filter(dps, "device-1").ToList();
            result.Should().HaveCount(3);
        }

        [Fact]
        public void Filter_NullDataPoints_ThrowsArgumentNullException()
        {
            var filter = CreateFilter();
            Action act = () => filter.Filter(null!, "device-1").ToList();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Filter_EmptyDeviceId_ThrowsArgumentException()
        {
            var filter = CreateFilter();
            Action act = () => filter.Filter(Array.Empty<DataPoint>(), "").ToList();
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Filter_DifferentDevices_TrackedSeparately()
        {
            var filter = CreateFilter(o =>
            {
                o.EnableDeduplication = true;
                o.DeduplicationWindow = TimeSpan.FromSeconds(60);
            });

            // Same name+value but different device IDs should both pass
            filter.Filter(new[] { Temp(value: 50.0) }, "device-1").ToList();
            var result = filter.Filter(new[] { Temp(value: 50.0) }, "device-2").ToList();
            result.Should().HaveCount(1);
        }
    }
}
