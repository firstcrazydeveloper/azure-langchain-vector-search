using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using IoTEdgeGateway.Application.Services;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using IoTEdgeGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace IoTEdgeGateway.Tests.Application
{
    public class CommandDispatchServiceTests : IAsyncDisposable
    {
        private readonly Mock<ICommandHandler> _handlerMock = new();
        private readonly Mock<IIoTHubClient> _hubMock = new();
        private readonly Mock<IProtocolAdapter> _adapterMock = new();
        private readonly CommandDispatchService _service;

        public CommandDispatchServiceTests()
        {
            _hubMock.SetupAdd(h => h.CommandReceived += It.IsAny<EventHandler<DeviceCommand>>());
            _hubMock.SetupRemove(h => h.CommandReceived -= It.IsAny<EventHandler<DeviceCommand>>());

            _service = new CommandDispatchService(
                new[] { _handlerMock.Object },
                _hubMock.Object,
                new[] { _adapterMock.Object },
                NullLogger<CommandDispatchService>.Instance);
        }

        private static DeviceCommand MakeCommand(string name = "Start",
            TimeSpan? timeout = null) =>
            new(new DeviceId("device-1"), name,
                new Dictionary<string, object>(), timeout);

        [Fact]
        public async Task DispatchAsync_HandlerCanHandle_ExecutesHandler()
        {
            _handlerMock.Setup(h => h.CanHandle("Start")).Returns(true);
            _handlerMock.Setup(h => h.HandleAsync(It.IsAny<DeviceCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var cmd = MakeCommand("Start");
            await _service.DispatchAsync(cmd);

            cmd.Status.Should().Be(CommandStatus.Succeeded);
        }

        [Fact]
        public async Task DispatchAsync_HandlerReturnsFalse_MarksCommandFailed()
        {
            _handlerMock.Setup(h => h.CanHandle("Stop")).Returns(true);
            _handlerMock.Setup(h => h.HandleAsync(It.IsAny<DeviceCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var cmd = MakeCommand("Stop");
            await _service.DispatchAsync(cmd);

            cmd.Status.Should().Be(CommandStatus.Failed);
        }

        [Fact]
        public async Task DispatchAsync_NoHandler_ForwardsToAdapter()
        {
            _handlerMock.Setup(h => h.CanHandle(It.IsAny<string>())).Returns(false);
            _adapterMock.Setup(a => a.IsConnected).Returns(true);
            _adapterMock.Setup(a => a.SendCommandAsync(It.IsAny<DeviceCommand>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var cmd = MakeCommand("CustomCmd");
            await _service.DispatchAsync(cmd);

            cmd.Status.Should().Be(CommandStatus.Succeeded);
            _adapterMock.Verify(a => a.SendCommandAsync(cmd, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DispatchAsync_NoAdapterConnected_MarksRejected()
        {
            _handlerMock.Setup(h => h.CanHandle(It.IsAny<string>())).Returns(false);
            _adapterMock.Setup(a => a.IsConnected).Returns(false);

            var cmd = MakeCommand("CustomCmd");
            await _service.DispatchAsync(cmd);

            cmd.Status.Should().Be(CommandStatus.Rejected);
        }

        [Fact]
        public async Task DispatchAsync_ExpiredCommand_MarksTimedOut()
        {
            var cmd = MakeCommand(timeout: TimeSpan.FromMilliseconds(1));
            await Task.Delay(50); // Let it expire

            await _service.DispatchAsync(cmd);

            cmd.Status.Should().Be(CommandStatus.TimedOut);
        }

        [Fact]
        public async Task DispatchAsync_HandlerThrows_MarksCommandFailed()
        {
            _handlerMock.Setup(h => h.CanHandle("Throw")).Returns(true);
            _handlerMock.Setup(h => h.HandleAsync(It.IsAny<DeviceCommand>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var cmd = MakeCommand("Throw");
            await _service.DispatchAsync(cmd);

            cmd.Status.Should().Be(CommandStatus.Failed);
            cmd.ErrorMessage.Should().Contain("boom");
        }

        [Fact]
        public async Task DispatchAsync_NullCommand_Throws()
        {
            Func<Task> act = () => _service.DispatchAsync(null!);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        public async ValueTask DisposeAsync() => await _service.DisposeAsync();
    }
}
