using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;

namespace IoTEdgeGateway.Domain.Interfaces
{
    public interface ICommandHandler
    {
        bool CanHandle(string commandName);
        Task<bool> HandleAsync(DeviceCommand command, CancellationToken cancellationToken = default);
    }
}
