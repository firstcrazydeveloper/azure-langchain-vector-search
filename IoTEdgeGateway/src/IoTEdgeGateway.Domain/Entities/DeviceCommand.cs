using System;
using System.Collections.Generic;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.ValueObjects;

namespace IoTEdgeGateway.Domain.Entities
{
    public sealed class DeviceCommand
    {
        public Guid CommandId { get; }
        public DeviceId TargetDeviceId { get; }
        public string CommandName { get; }
        public IReadOnlyDictionary<string, object> Parameters { get; }
        public CommandStatus Status { get; private set; }
        public DateTimeOffset IssuedAt { get; }
        public DateTimeOffset? ExecutedAt { get; private set; }
        public string? ErrorMessage { get; private set; }
        public TimeSpan Timeout { get; }

        public DeviceCommand(
            DeviceId targetDeviceId,
            string commandName,
            Dictionary<string, object>? parameters = null,
            TimeSpan? timeout = null)
        {
            CommandId = Guid.NewGuid();
            TargetDeviceId = targetDeviceId ?? throw new ArgumentNullException(nameof(targetDeviceId));
            CommandName = string.IsNullOrWhiteSpace(commandName)
                ? throw new ArgumentException("Command name cannot be empty.", nameof(commandName))
                : commandName;
            Parameters = parameters ?? new Dictionary<string, object>();
            Status = CommandStatus.Pending;
            IssuedAt = DateTimeOffset.UtcNow;
            Timeout = timeout ?? TimeSpan.FromSeconds(30);
        }

        public void MarkExecuting() => Status = CommandStatus.Executing;

        public void MarkSucceeded()
        {
            Status = CommandStatus.Succeeded;
            ExecutedAt = DateTimeOffset.UtcNow;
        }

        public void MarkFailed(string errorMessage)
        {
            Status = CommandStatus.Failed;
            ExecutedAt = DateTimeOffset.UtcNow;
            ErrorMessage = errorMessage;
        }

        public void MarkRejected(string reason)
        {
            Status = CommandStatus.Rejected;
            ErrorMessage = reason;
        }

        public void MarkTimedOut()
        {
            Status = CommandStatus.TimedOut;
            ErrorMessage = $"Command timed out after {Timeout.TotalSeconds}s";
        }

        public bool IsExpired() =>
            DateTimeOffset.UtcNow - IssuedAt > Timeout && Status == CommandStatus.Pending;
    }
}
