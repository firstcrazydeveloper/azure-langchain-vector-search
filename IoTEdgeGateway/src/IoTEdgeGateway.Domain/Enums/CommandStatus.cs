namespace IoTEdgeGateway.Domain.Enums
{
    public enum CommandStatus
    {
        Pending,
        Executing,
        Succeeded,
        Failed,
        Rejected,
        TimedOut
    }
}
