using System.Collections.Generic;
using IoTEdgeGateway.Domain.Entities;

namespace IoTEdgeGateway.Domain.Interfaces
{
    public interface IDataFilter
    {
        IEnumerable<DataPoint> Filter(IEnumerable<DataPoint> dataPoints, string deviceId);
    }
}
