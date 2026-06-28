using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.ValueObjects;

namespace IoTEdgeGateway.Domain.Interfaces
{
    public interface IAlertService
    {
        Task RaiseAlertAsync(AlertEvent alert, CancellationToken cancellationToken = default);
        Task AcknowledgeAlertAsync(Guid alertId, CancellationToken cancellationToken = default);
        Task<IEnumerable<AlertEvent>> GetActiveAlertsAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<AlertEvent>> GetAlertsByDeviceAsync(DeviceId deviceId, CancellationToken cancellationToken = default);
        Task EvaluateDataPointsAsync(DeviceId deviceId, IEnumerable<DataPoint> dataPoints, CancellationToken cancellationToken = default);

        event EventHandler<AlertEvent>? AlertRaised;
    }
}
