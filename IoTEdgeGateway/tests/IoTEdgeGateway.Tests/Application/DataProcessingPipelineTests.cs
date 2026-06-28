using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using IoTEdgeGateway.Application.Pipeline;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using IoTEdgeGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoTEdgeGateway.Tests.Application
{
    public class DataProcessingPipelineTests
    {
        private readonly Mock<IDataTranslator> _translatorMock = new();
        private readonly Mock<IDataFilter> _filterMock = new();
        private readonly Mock<IDataValidator> _validatorMock = new();
        private readonly Mock<IAlertService> _alertMock = new();
        private readonly Mock<IIoTHubClient> _hubMock = new();

        private DataProcessingPipeline CreatePipeline()
        {
            return new DataProcessingPipeline(
                new[] { _translatorMock.Object },
                _filterMock.Object,
                _validatorMock.Object,
                _alertMock.Object,
                _hubMock.Object,
                NullLogger<DataProcessingPipeline>.Instance);
        }

        private static DataPoint GoodTemp(double value = 50.0) =>
            new("temperature", value, DataPointType.Temperature, "°C");

        private static DeviceMessage MakeMessage(ProtocolType protocol = ProtocolType.Mqtt) =>
            new(new DeviceId("device-1"), protocol, Array.Empty<DataPoint>(), "{}");

        [Fact]
        public async Task ProcessAsync_HappyPath_SendsTelemetryAndReturnsResult()
        {
            var dp = GoodTemp();
            _translatorMock.Setup(t => t.SupportedProtocol).Returns(ProtocolType.Mqtt);
            _translatorMock.Setup(t => t.Translate(It.IsAny<DeviceMessage>())).Returns(new[] { dp });
            _filterMock.Setup(f => f.Filter(It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<string>()))
                .Returns(new[] { dp });
            _validatorMock.Setup(v => v.Validate(dp))
                .Returns(Domain.ValueObjects.ValidationResult.Success());
            _alertMock.Setup(a => a.EvaluateDataPointsAsync(
                It.IsAny<DeviceId>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _hubMock.Setup(h => h.SendTelemetryAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var result = await CreatePipeline().ProcessAsync(MakeMessage());

            result.ValidDataPoints.Should().HaveCount(1);
            result.InvalidDataPoints.Should().BeEmpty();
            result.SentToCloud.Should().BeTrue();
            _hubMock.Verify(h => h.SendTelemetryAsync("device-1", It.IsAny<IEnumerable<DataPoint>>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ProcessAsync_ValidationFails_MarksAsInvalid()
        {
            var dp = GoodTemp();
            _translatorMock.Setup(t => t.SupportedProtocol).Returns(ProtocolType.Mqtt);
            _translatorMock.Setup(t => t.Translate(It.IsAny<DeviceMessage>())).Returns(new[] { dp });
            _filterMock.Setup(f => f.Filter(It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<string>()))
                .Returns(new[] { dp });
            _validatorMock.Setup(v => v.Validate(dp))
                .Returns(Domain.ValueObjects.ValidationResult.Failure(new[] { "Out of range" }));
            _alertMock.Setup(a => a.EvaluateDataPointsAsync(
                It.IsAny<DeviceId>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var result = await CreatePipeline().ProcessAsync(MakeMessage());

            result.InvalidDataPoints.Should().HaveCount(1);
            result.ValidDataPoints.Should().BeEmpty();
            result.SentToCloud.Should().BeFalse();
            _hubMock.Verify(h => h.SendTelemetryAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessAsync_ThresholdExceeded_RaisesAlert()
        {
            var dp = new DataPoint("temperature", 200.0, DataPointType.Temperature,
                "°C", maxThreshold: 100.0);

            _translatorMock.Setup(t => t.SupportedProtocol).Returns(ProtocolType.Mqtt);
            _translatorMock.Setup(t => t.Translate(It.IsAny<DeviceMessage>())).Returns(new[] { dp });
            _filterMock.Setup(f => f.Filter(It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<string>()))
                .Returns(new[] { dp });
            _validatorMock.Setup(v => v.Validate(dp))
                .Returns(Domain.ValueObjects.ValidationResult.Success());
            _alertMock.Setup(a => a.EvaluateDataPointsAsync(
                It.IsAny<DeviceId>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _alertMock.Setup(a => a.RaiseAlertAsync(It.IsAny<AlertEvent>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _hubMock.Setup(h => h.SendTelemetryAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var result = await CreatePipeline().ProcessAsync(MakeMessage());

            result.GeneratedAlerts.Should().HaveCount(1);
            result.GeneratedAlerts[0].Severity.Should().Be(AlertSeverity.Critical);
            _alertMock.Verify(a => a.RaiseAlertAsync(It.IsAny<AlertEvent>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task ProcessAsync_NoTranslatorForProtocol_UsesRawDataPoints()
        {
            _translatorMock.Setup(t => t.SupportedProtocol).Returns(ProtocolType.Mqtt);

            var rawDp = GoodTemp();
            var message = new DeviceMessage(
                new DeviceId("device-1"), ProtocolType.ModbusTcp, new[] { rawDp }, "");

            _filterMock.Setup(f => f.Filter(It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<string>()))
                .Returns(new[] { rawDp });
            _validatorMock.Setup(v => v.Validate(It.IsAny<DataPoint>()))
                .Returns(Domain.ValueObjects.ValidationResult.Success());
            _alertMock.Setup(a => a.EvaluateDataPointsAsync(
                It.IsAny<DeviceId>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _hubMock.Setup(h => h.SendTelemetryAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var result = await CreatePipeline().ProcessAsync(message);
            result.ValidDataPoints.Should().HaveCount(1);
        }

        [Fact]
        public async Task ProcessAsync_IoTHubFails_StillReturnsLocalResults()
        {
            var dp = GoodTemp();
            _translatorMock.Setup(t => t.SupportedProtocol).Returns(ProtocolType.Mqtt);
            _translatorMock.Setup(t => t.Translate(It.IsAny<DeviceMessage>())).Returns(new[] { dp });
            _filterMock.Setup(f => f.Filter(It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<string>()))
                .Returns(new[] { dp });
            _validatorMock.Setup(v => v.Validate(dp))
                .Returns(Domain.ValueObjects.ValidationResult.Success());
            _alertMock.Setup(a => a.EvaluateDataPointsAsync(
                It.IsAny<DeviceId>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _hubMock.Setup(h => h.SendTelemetryAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<DataPoint>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Hub unreachable"));

            var result = await CreatePipeline().ProcessAsync(MakeMessage());

            result.ValidDataPoints.Should().HaveCount(1);
            result.SentToCloud.Should().BeFalse();
        }

        [Fact]
        public async Task ProcessAsync_NullMessage_Throws()
        {
            Func<Task> act = () => CreatePipeline().ProcessAsync(null!);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }
    }
}
