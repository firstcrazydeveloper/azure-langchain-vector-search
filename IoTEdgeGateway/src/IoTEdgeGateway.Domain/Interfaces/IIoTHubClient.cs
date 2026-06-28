using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;

namespace IoTEdgeGateway.Domain.Interfaces
{
    public interface IIoTHubClient : IAsyncDisposable
    {
        bool IsConnected { get; }

        Task ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync(CancellationToken cancellationToken = default);
        Task SendTelemetryAsync(string deviceId, IEnumerable<DataPoint> dataPoints, CancellationToken cancellationToken = default);
        Task SendAlertAsync(AlertEvent alert, CancellationToken cancellationToken = default);
        Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> properties, CancellationToken cancellationToken = default);

        event EventHandler<DeviceCommand>? CommandReceived;
        event EventHandler<bool>? ConnectionStateChanged;
    }
}
