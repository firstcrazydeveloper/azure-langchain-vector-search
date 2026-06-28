using System;

namespace IoTEdgeGateway.Domain.Exceptions
{
    public class GatewayException : Exception
    {
        public string ErrorCode { get; }

        public GatewayException(string message, string errorCode = "GATEWAY_ERROR")
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public GatewayException(string message, Exception innerException, string errorCode = "GATEWAY_ERROR")
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    public class ProtocolAdapterException : GatewayException
    {
        public string Protocol { get; }

        public ProtocolAdapterException(string protocol, string message, Exception? inner = null)
            : base(message, inner ?? new Exception(message), $"PROTOCOL_{protocol.ToUpperInvariant()}_ERROR")
        {
            Protocol = protocol;
        }
    }

    public class DataValidationException : GatewayException
    {
        public DataValidationException(string message)
            : base(message, "DATA_VALIDATION_ERROR") { }
    }

    public class IoTHubCommunicationException : GatewayException
    {
        public IoTHubCommunicationException(string message, Exception? inner = null)
            : base(message, inner ?? new Exception(message), "IOTHUB_COMM_ERROR") { }
    }

    public class CommandExecutionException : GatewayException
    {
        public string CommandName { get; }

        public CommandExecutionException(string commandName, string message, Exception? inner = null)
            : base(message, inner ?? new Exception(message), "COMMAND_EXEC_ERROR")
        {
            CommandName = commandName;
        }
    }
}
