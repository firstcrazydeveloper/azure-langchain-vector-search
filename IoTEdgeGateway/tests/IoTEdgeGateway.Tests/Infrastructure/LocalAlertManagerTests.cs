using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using IoTEdgeGateway.Domain.ValueObjects;
using IoTEdgeGateway.Infrastructure.Alert;
using IoTEdgeGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace IoTEdgeGateway.Tests.Infrastructure
{
    public class LocalAlertManagerTests
    {
        private readonly Mock<IIoTHubClient> _hubMock = new();

        private LocalAlertManager CreateManager(Action<AlertConfig>? configure = null)
        {
            var cfg = new GatewayConfiguration();
            configure?.Invoke(cfg.Alert);

            _hubMock.Setup(h => h.SendAlertAsync(It.IsAny<AlertEvent>(), default))
                .Returns(Task.CompletedTask);

            return new LocalAlertManager(
                Options.Create(cfg),
                _hubMock.Object,
                NullLogger<LocalAlertManager>.Instance);
        }

        private static AlertEvent MakeAlert(string dataPointName = "temperature",
            AlertSeverity severity = AlertSeverity.Warning) =>
            new(new DeviceId("device-1"), severity, "Threshold breached", dataPointName);

        [Fact]
        public async Task RaiseAlert_NewAlert_AddsToActiveAlerts()
        {
            var manager = CreateManager(c => c.AlertCooldownSeconds = 0);
            var alert = MakeAlert();

            await manager.RaiseAlertAsync(alert);
            var active = (await manager.GetActiveAlertsAsync()).ToList();

            active.Should().HaveCount(1);
            active[0].AlertId.Should().Be(alert.AlertId);
        }

        [Fact]
        public async Task RaiseAlert_SameAlertWithinCooldown_Suppressed()
        {
            var manager = CreateManager(c => c.AlertCooldownSeconds = 300);

            await manager.RaiseAlertAsync(MakeAlert("temperature"));
            await manager.RaiseAlertAsync(MakeAlert("temperature")); // same device+datapoint

            var active = (await manager.GetActiveAlertsAsync()).ToList();
            active.Should().HaveCount(1); // only one gets through
        }

        [Fact]
        public async Task RaiseAlert_DifferentDataPoints_BothRaised()
        {
            var manager = CreateManager(c => c.AlertCooldownSeconds = 300);

            await manager.RaiseAlertAsync(MakeAlert("temperature"));
            await manager.RaiseAlertAsync(MakeAlert("pressure"));

            var active = (await manager.GetActiveAlertsAsync()).ToList();
            active.Should().HaveCount(2);
        }

        [Fact]
        public async Task RaiseAlert_SendsToCloud_WhenEnabled()
        {
            var manager = CreateManager(c =>
            {
                c.SendAlertsToCloud = true;
                c.AlertCooldownSeconds = 0;
            });

            await manager.RaiseAlertAsync(MakeAlert());

            _hubMock.Verify(h => h.SendAlertAsync(It.IsAny<AlertEvent>(), default), Times.Once);
        }

        [Fact]
        public async Task RaiseAlert_DoesNotSendToCloud_WhenDisabled()
        {
            var manager = CreateManager(c =>
            {
                c.SendAlertsToCloud = false;
                c.AlertCooldownSeconds = 0;
            });

            await manager.RaiseAlertAsync(MakeAlert());

            _hubMock.Verify(h => h.SendAlertAsync(It.IsAny<AlertEvent>(), default), Times.Never);
        }

        [Fact]
        public async Task AcknowledgeAlert_ExistingAlert_MarksAcknowledged()
        {
            var manager = CreateManager(c => c.AlertCooldownSeconds = 0);
            var alert = MakeAlert();
            await manager.RaiseAlertAsync(alert);

            await manager.AcknowledgeAlertAsync(alert.AlertId);

            var active = (await manager.GetActiveAlertsAsync()).ToList();
            active.Should().BeEmpty(); // acknowledged alerts are filtered out
        }

        [Fact]
        public async Task GetAlertsByDevice_ReturnsOnlyMatchingDevice()
        {
            var manager = CreateManager(c => c.AlertCooldownSeconds = 0);

            var alert1 = new AlertEvent(new DeviceId("dev-A"), AlertSeverity.Warning, "msg", "dp1");
            var alert2 = new AlertEvent(new DeviceId("dev-B"), AlertSeverity.Warning, "msg", "dp2");

            await manager.RaiseAlertAsync(alert1);
            await manager.RaiseAlertAsync(alert2);

            var devAAlerts = (await manager.GetAlertsByDeviceAsync(new DeviceId("dev-A"))).ToList();
            devAAlerts.Should().HaveCount(1);
            devAAlerts[0].DeviceId.Value.Should().Be("dev-A");
        }

        [Fact]
        public async Task AlertRaised_Event_FiresOnNewAlert()
        {
            var manager = CreateManager(c => c.AlertCooldownSeconds = 0);
            AlertEvent? received = null;
            manager.AlertRaised += (_, e) => received = e;

            var alert = MakeAlert();
            await manager.RaiseAlertAsync(alert);

            received.Should().NotBeNull();
            received!.AlertId.Should().Be(alert.AlertId);
        }

        [Fact]
        public async Task EvaluateDataPoints_HighTempAndVibration_RaisesCriticalAlert()
        {
            var manager = CreateManager(c => c.AlertCooldownSeconds = 0);
            AlertEvent? raised = null;
            manager.AlertRaised += (_, e) => raised = e;

            var points = new[]
            {
                new DataPoint("temperature", 110.0, DataPointType.Temperature),
                new DataPoint("vibration", 8.0, DataPointType.Vibration)
            };

            await manager.EvaluateDataPointsAsync(new DeviceId("dev-1"), points);

            raised.Should().NotBeNull();
            raised!.Severity.Should().Be(AlertSeverity.Critical);
            raised.DataPointName.Should().Contain("temp-vib");
        }

        [Fact]
        public async Task EvaluateDataPoints_HighCurrentLowSpeed_RaisesEmergencyAlert()
        {
            var manager = CreateManager(c => c.AlertCooldownSeconds = 0);
            AlertEvent? raised = null;
            manager.AlertRaised += (_, e) => raised = e;

            var points = new[]
            {
                new DataPoint("current", 80.0, DataPointType.Current),
                new DataPoint("speed", 5.0, DataPointType.Speed)
            };

            await manager.EvaluateDataPointsAsync(new DeviceId("dev-1"), points);

            raised.Should().NotBeNull();
            raised!.Severity.Should().Be(AlertSeverity.Emergency);
        }

        [Fact]
        public async Task RaiseAlert_NullAlert_Throws()
        {
            var manager = CreateManager();
            Func<Task> act = () => manager.RaiseAlertAsync(null!);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }
    }
}
