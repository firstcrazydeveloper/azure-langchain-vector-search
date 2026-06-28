using System.Collections.Generic;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.ValueObjects;

namespace IoTEdgeGateway.Domain.Interfaces
{
    public interface IDataValidator
    {
        ValidationResult Validate(DataPoint dataPoint);
        IEnumerable<ValidationResult> ValidateAll(IEnumerable<DataPoint> dataPoints);
    }
}
