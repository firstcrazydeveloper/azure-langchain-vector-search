using System;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;

namespace IoTEdgeGateway.Domain.Interfaces
{
    public interface IProtocolAdapter : IAsyncDisposable
    {
        ProtocolType Protocol { get; }
        bool IsConnected { get; }

        Task ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync(CancellationToken cancellationToken = default);
        Task SendCommandAsync(DeviceCommand command, CancellationToken cancellationToken = default);

        event EventHandler<DeviceMessage>? MessageReceived;
        event EventHandler<Exception>? ErrorOccurred;
        event EventHandler<bool>? ConnectionStateChanged;
    }
}
