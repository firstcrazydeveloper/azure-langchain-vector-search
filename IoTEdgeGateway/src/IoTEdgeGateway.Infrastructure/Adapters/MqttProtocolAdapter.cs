using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Exceptions;
using IoTEdgeGateway.Domain.Interfaces;
using IoTEdgeGateway.Domain.ValueObjects;
using IoTEdgeGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;

namespace IoTEdgeGateway.Infrastructure.Adapters
{
    public sealed class MqttProtocolAdapter : IProtocolAdapter
    {
        private readonly MqttAdapterConfig _config;
        private readonly ILogger<MqttProtocolAdapter> _logger;
        private readonly IMqttClient _mqttClient;
        private bool _disposed;

        public ProtocolType Protocol => ProtocolType.Mqtt;
        public bool IsConnected => _mqttClient.IsConnected;

        public event EventHandler<DeviceMessage>? MessageReceived;
        public event EventHandler<Exception>? ErrorOccurred;
        public event EventHandler<bool>? ConnectionStateChanged;

        public MqttProtocolAdapter(IOptions<GatewayConfiguration> config,
            ILogger<MqttProtocolAdapter> logger,
            IMqttClient? mqttClient = null)
        {
            _config = config?.Value?.Mqtt ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mqttClient = mqttClient ?? new MqttFactory().CreateMqttClient();

            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
            _mqttClient.ConnectedAsync += OnConnectedAsync;
            _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.BrokerHost, _config.BrokerPort)
                .WithClientId(_config.ClientId)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(_config.KeepAliveSeconds))
                .WithCleanSession();

            if (!string.IsNullOrWhiteSpace(_config.Username))
                options.WithCredentials(_config.Username, _config.Password);

            if (_config.UseTls)
                options.WithTlsOptions(o => o.UseTls());

            var builtOptions = options.Build();

            try
            {
                _logger.LogInformation("Connecting to MQTT broker {Host}:{Port}...",
                    _config.BrokerHost, _config.BrokerPort);

                await _mqttClient.ConnectAsync(builtOptions, cancellationToken);

                // Subscribe to all configured topics
                foreach (var topic in _config.TopicsToSubscribe)
                {
                    await _mqttClient.SubscribeAsync(
                        new MqttTopicFilterBuilder()
                            .WithTopic(topic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .Build(),
                        cancellationToken);

                    _logger.LogInformation("Subscribed to MQTT topic: {Topic}", topic);
                }
            }
            catch (MqttCommunicationException ex)
            {
                throw new ProtocolAdapterException("MQTT",
                    $"Failed to connect to broker {_config.BrokerHost}:{_config.BrokerPort}", ex);
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync(
                    new MqttClientDisconnectOptionsBuilder()
                        .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                        .Build(),
                    cancellationToken);
            }
        }

        public async Task SendCommandAsync(DeviceCommand command, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!IsConnected)
                throw new ProtocolAdapterException("MQTT", "Cannot send command: not connected.");

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                commandId = command.CommandId,
                commandName = command.CommandName,
                parameters = command.Parameters,
                issuedAt = command.IssuedAt
            });

            var topic = $"devices/{command.TargetDeviceId}/commands/{command.CommandName}";
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, cancellationToken);
            _logger.LogInformation("MQTT command '{Command}' published to {Topic}.", command.CommandName, topic);
        }

        private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var topic = args.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

                // Extract device ID from topic pattern: devices/{deviceId}/telemetry
                var deviceId = ExtractDeviceIdFromTopic(topic) ?? "unknown";

                var message = new DeviceMessage(
                    new DeviceId(deviceId),
                    ProtocolType.Mqtt,
                    Array.Empty<DataPoint>(),
                    rawPayload: payload,
                    metadata: new Dictionary<string, string>
                    {
                        ["mqtt.topic"] = topic,
                        ["mqtt.qos"] = args.ApplicationMessage.QualityOfServiceLevel.ToString()
                    });

                MessageReceived?.Invoke(this, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing MQTT message.");
                ErrorOccurred?.Invoke(this, ex);
            }

            return Task.CompletedTask;
        }

        private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
        {
            _logger.LogInformation("MQTT connected to {Host}:{Port}.", _config.BrokerHost, _config.BrokerPort);
            ConnectionStateChanged?.Invoke(this, true);
            return Task.CompletedTask;
        }

        private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            _logger.LogWarning("MQTT disconnected. Reason: {Reason}", args.Reason);
            ConnectionStateChanged?.Invoke(this, false);

            // Auto-reconnect logic with exponential backoff
            if (args.ClientWasConnected && !_disposed)
            {
                for (var attempt = 1; attempt <= _config.MaxReconnectAttempts; attempt++)
                {
                    try
                    {
                        var delay = TimeSpan.FromSeconds(
                            Math.Min(_config.ReconnectDelaySeconds * Math.Pow(2, attempt - 1), 120));

                        _logger.LogInformation("MQTT reconnect attempt {Attempt}/{Max} in {Delay}s...",
                            attempt, _config.MaxReconnectAttempts, delay.TotalSeconds);

                        await Task.Delay(delay);
                        await ConnectAsync();
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "MQTT reconnect attempt {Attempt} failed.", attempt);
                        ErrorOccurred?.Invoke(this, ex);
                    }
                }

                _logger.LogError("MQTT: all {Max} reconnect attempts exhausted.", _config.MaxReconnectAttempts);
            }
        }

        private static string? ExtractDeviceIdFromTopic(string topic)
        {
            // Pattern: devices/{deviceId}/telemetry
            var parts = topic.Split('/');
            if (parts.Length >= 3 && parts[0] == "devices")
                return parts[1];
            return null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MqttProtocolAdapter));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await DisconnectAsync();
            _mqttClient.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
            _mqttClient.ConnectedAsync -= OnConnectedAsync;
            _mqttClient.DisconnectedAsync -= OnDisconnectedAsync;
            _mqttClient.Dispose();
        }
    }
}
