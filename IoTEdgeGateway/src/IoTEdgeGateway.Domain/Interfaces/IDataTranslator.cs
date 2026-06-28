using System.Collections.Generic;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;

namespace IoTEdgeGateway.Domain.Interfaces
{
    public interface IDataTranslator
    {
        ProtocolType SupportedProtocol { get; }
        IEnumerable<DataPoint> Translate(DeviceMessage message);
    }
}
