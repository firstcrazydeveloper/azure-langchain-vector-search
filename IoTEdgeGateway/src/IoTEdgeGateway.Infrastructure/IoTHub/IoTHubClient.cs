using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Exceptions;
using IoTEdgeGateway.Domain.Interfaces;
using IoTEdgeGateway.Domain.ValueObjects;
using IoTEdgeGateway.Infrastructure.Configuration;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IoTEdgeGateway.Infrastructure.IoTHub
{
    public sealed class IoTHubClient : IIoTHubClient
    {
        private readonly IoTHubConfig _config;
        private readonly ILogger<IoTHubClient> _logger;
        private ModuleClient? _moduleClient;
        private bool _connected;
        private bool _disposed;

        public bool IsConnected => _connected;

        public event EventHandler<DeviceCommand>? CommandReceived;
        public event EventHandler<bool>? ConnectionStateChanged;

        public IoTHubClient(IOptions<GatewayConfiguration> config, ILogger<IoTHubClient> logger)
        {
            _config = config?.Value?.IoTHub ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            try
            {
                _logger.LogInformation("Connecting to IoT Hub...");

                _moduleClient = string.IsNullOrWhiteSpace(_config.ConnectionString)
                    ? await ModuleClient.CreateFromEnvironmentAsync(TransportType.Amqp)
                    : ModuleClient.CreateFromConnectionString(_config.ConnectionString, TransportType.Amqp);

                if (!string.IsNullOrWhiteSpace(_config.ModelId))
                {
                    var options = new ClientOptions { ModelId = _config.ModelId };
                    _moduleClient = ModuleClient.CreateFromConnectionString(
                        _config.ConnectionString, TransportType.Amqp, options);
                }

                await _moduleClient.OpenAsync(cancellationToken);

                // Register direct method handlers (Cloud-to-Device commands)
                await _moduleClient.SetMethodDefaultHandlerAsync(OnDirectMethodReceived, null, cancellationToken);

                // Register desired property update handler
                await _moduleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertyUpdated, null, cancellationToken);

                // Connection status change handler
                _moduleClient.SetConnectionStatusChangesHandler(OnConnectionStatusChanged);

                _connected = true;
                ConnectionStateChanged?.Invoke(this, true);
                _logger.LogInformation("IoT Hub connected successfully.");
            }
            catch (Exception ex)
            {
                throw new IoTHubCommunicationException("Failed to connect to IoT Hub.", ex);
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_moduleClient is not null)
            {
                await _moduleClient.CloseAsync(cancellationToken);
                _connected = false;
                ConnectionStateChanged?.Invoke(this, false);
            }
        }

        public async Task SendTelemetryAsync(string deviceId,
            IEnumerable<DataPoint> dataPoints, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_moduleClient is null || !_connected)
                throw new IoTHubCommunicationException("IoT Hub client is not connected.");

            var payload = new
            {
                deviceId,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                dataPoints = dataPoints.Select(dp => new
                {
                    name = dp.Name,
                    value = dp.Value,
                    type = dp.Type.ToString(),
                    unit = dp.Unit,
                    timestamp = dp.Timestamp.ToString("O")
                })
            };

            var json = JsonSerializer.Serialize(payload);
            var message = new Message(Encoding.UTF8.GetBytes(json))
            {
                ContentEncoding = "utf-8",
                ContentType = "application/json"
            };

            message.Properties["deviceId"] = deviceId;
            message.Properties["messageType"] = "telemetry";

            await RetryAsync(() => _moduleClient.SendEventAsync(message, cancellationToken),
                "SendTelemetry", cancellationToken);

            _logger.LogDebug("Telemetry sent: {Count} data points from {DeviceId}.",
                dataPoints.Count(), deviceId);
        }

        public async Task SendAlertAsync(AlertEvent alert, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_moduleClient is null || !_connected)
            {
                _logger.LogWarning("IoT Hub not connected. Alert {AlertId} not sent.", alert.AlertId);
                return;
            }

            var payload = new
            {
                alertId = alert.AlertId,
                deviceId = alert.DeviceId.Value,
                severity = alert.Severity.ToString(),
                message = alert.Message,
                dataPointName = alert.DataPointName,
                actualValue = alert.ActualValue,
                thresholdValue = alert.ThresholdValue,
                occurredAt = alert.OccurredAt.ToString("O")
            };

            var json = JsonSerializer.Serialize(payload);
            var message = new Message(Encoding.UTF8.GetBytes(json))
            {
                ContentEncoding = "utf-8",
                ContentType = "application/json"
            };

            message.Properties["messageType"] = "alert";
            message.Properties["severity"] = alert.Severity.ToString();

            await RetryAsync(() => _moduleClient.SendEventAsync(message, cancellationToken),
                "SendAlert", cancellationToken);

            _logger.LogInformation("Alert {AlertId} sent to IoT Hub.", alert.AlertId);
        }

        public async Task UpdateDeviceTwinAsync(string deviceId,
            Dictionary<string, object> properties, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_moduleClient is null || !_connected)
                throw new IoTHubCommunicationException("IoT Hub client is not connected.");

            var patch = new TwinCollection();
            foreach (var (key, value) in properties)
                patch[key] = value;

            await _moduleClient.UpdateReportedPropertiesAsync(patch, cancellationToken);
            _logger.LogDebug("Device twin updated for {DeviceId} with {Count} properties.", deviceId, properties.Count);
        }

        private Task<MethodResponse> OnDirectMethodReceived(MethodRequest request, object? userContext)
        {
            _logger.LogInformation("Direct method received: {MethodName}", request.Name);

            try
            {
                var parameters = string.IsNullOrWhiteSpace(request.DataAsJson)
                    ? new Dictionary<string, object>()
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(request.DataAsJson)
                      ?? new Dictionary<string, object>();

                // Convert JsonElement values to native types
                var nativeParams = new Dictionary<string, object>();
                foreach (var (k, v) in parameters)
                {
                    nativeParams[k] = v is JsonElement je ? UnwrapJsonElement(je) : v;
                }

                // Extract target device from method name or parameters
                var targetDeviceId = nativeParams.TryGetValue("targetDeviceId", out var td)
                    ? td.ToString()! : "default-device";

                var command = new DeviceCommand(
                    new DeviceId(targetDeviceId),
                    request.Name,
                    nativeParams);

                CommandReceived?.Invoke(this, command);

                var response = JsonSerializer.Serialize(new
                {
                    commandId = command.CommandId,
                    status = "accepted",
                    timestamp = DateTimeOffset.UtcNow
                });

                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(response), 202));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing direct method {MethodName}.", request.Name);
                var error = JsonSerializer.Serialize(new { error = ex.Message });
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(error), 500));
            }
        }

        private Task OnDesiredPropertyUpdated(TwinCollection desiredProperties, object? userContext)
        {
            _logger.LogInformation("Device twin desired properties updated. Version: {Version}",
                desiredProperties.Version);

            // Emit as a configuration command
            var parameters = new Dictionary<string, object>
            {
                ["twinProperties"] = desiredProperties.ToJson(),
                ["version"] = desiredProperties.Version
            };

            var command = new DeviceCommand(
                new DeviceId("gateway"),
                "UpdateConfiguration",
                parameters);

            CommandReceived?.Invoke(this, command);
            return Task.CompletedTask;
        }

        private void OnConnectionStatusChanged(ConnectionStatus status, ConnectionStatusChangeReason reason)
        {
            _connected = status == ConnectionStatus.Connected;
            _logger.LogInformation("IoT Hub connection status: {Status}, reason: {Reason}", status, reason);
            ConnectionStateChanged?.Invoke(this, _connected);
        }

        private async Task RetryAsync(Func<Task> action, string operationName,
            CancellationToken cancellationToken)
        {
            var delay = TimeSpan.FromMilliseconds(_config.RetryBaseDelayMs);

            for (var attempt = 1; attempt <= _config.RetryMaxAttempts; attempt++)
            {
                try
                {
                    await action();
                    return;
                }
                catch (Exception ex) when (attempt < _config.RetryMaxAttempts)
                {
                    _logger.LogWarning(ex, "IoT Hub {Op} attempt {Attempt}/{Max} failed. Retrying in {Delay}ms.",
                        operationName, attempt, _config.RetryMaxAttempts, delay.TotalMilliseconds);

                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 60000));
                }
            }

            throw new IoTHubCommunicationException($"IoT Hub operation '{operationName}' failed after {_config.RetryMaxAttempts} attempts.");
        }

        private static object UnwrapJsonElement(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            _ => element.GetRawText()
        };

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IoTHubClient));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await DisconnectAsync();
            _moduleClient?.Dispose();
        }
    }
}
