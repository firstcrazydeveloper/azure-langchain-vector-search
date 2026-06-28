using System;
using System.Threading.Tasks;
using FluentAssertions;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.ValueObjects;
using Xunit;

namespace IoTEdgeGateway.Tests.Domain
{
    public class DeviceCommandTests
    {
        private static DeviceId AnyDevice => new("device-1");

        [Fact]
        public void Constructor_ValidArguments_InitializesPending()
        {
            var cmd = new DeviceCommand(AnyDevice, "Start");
            cmd.Status.Should().Be(CommandStatus.Pending);
            cmd.CommandName.Should().Be("Start");
            cmd.IssuedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void Constructor_NullDeviceId_Throws()
        {
            Action act = () => new DeviceCommand(null!, "cmd");
            act.Should().Throw<ArgumentNullException>();
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Constructor_EmptyCommandName_Throws(string? name)
        {
            Action act = () => new DeviceCommand(AnyDevice, name!);
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void MarkExecuting_ChangesStatusToExecuting()
        {
            var cmd = new DeviceCommand(AnyDevice, "Run");
            cmd.MarkExecuting();
            cmd.Status.Should().Be(CommandStatus.Executing);
        }

        [Fact]
        public void MarkSucceeded_SetsStatusAndTimestamp()
        {
            var cmd = new DeviceCommand(AnyDevice, "Run");
            cmd.MarkSucceeded();
            cmd.Status.Should().Be(CommandStatus.Succeeded);
            cmd.ExecutedAt.Should().NotBeNull();
        }

        [Fact]
        public void MarkFailed_SetsErrorMessage()
        {
            var cmd = new DeviceCommand(AnyDevice, "Run");
            cmd.MarkFailed("network error");
            cmd.Status.Should().Be(CommandStatus.Failed);
            cmd.ErrorMessage.Should().Be("network error");
        }

        [Fact]
        public void MarkRejected_SetsRejectStatus()
        {
            var cmd = new DeviceCommand(AnyDevice, "Run");
            cmd.MarkRejected("not supported");
            cmd.Status.Should().Be(CommandStatus.Rejected);
            cmd.ErrorMessage.Should().Contain("not supported");
        }

        [Fact]
        public void MarkTimedOut_SetsTimedOutStatus()
        {
            var cmd = new DeviceCommand(AnyDevice, "Run");
            cmd.MarkTimedOut();
            cmd.Status.Should().Be(CommandStatus.TimedOut);
            cmd.ErrorMessage.Should().Contain("timed out");
        }

        [Fact]
        public async Task IsExpired_PendingAfterTimeout_ReturnsTrue()
        {
            var cmd = new DeviceCommand(AnyDevice, "Run", timeout: TimeSpan.FromMilliseconds(50));
            await Task.Delay(100);
            cmd.IsExpired().Should().BeTrue();
        }

        [Fact]
        public void IsExpired_FreshCommand_ReturnsFalse()
        {
            var cmd = new DeviceCommand(AnyDevice, "Run", timeout: TimeSpan.FromSeconds(30));
            cmd.IsExpired().Should().BeFalse();
        }

        [Fact]
        public void IsExpired_ExecutingCommand_ReturnsFalse()
        {
            // Only Pending commands can expire via IsExpired
            var cmd = new DeviceCommand(AnyDevice, "Run", timeout: TimeSpan.FromMilliseconds(1));
            cmd.MarkExecuting();
            cmd.IsExpired().Should().BeFalse();
        }
    }
}
