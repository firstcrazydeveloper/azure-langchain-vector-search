using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace IoTEdgeGateway.Infrastructure.Adapters
{
    public sealed class OpcUaProtocolAdapter : IProtocolAdapter
    {
        private readonly OpcUaAdapterConfig _config;
        private readonly ILogger<OpcUaProtocolAdapter> _logger;
        private Session? _session;
        private Subscription? _subscription;
        private bool _disposed;

        public ProtocolType Protocol => ProtocolType.OpcUa;
        public bool IsConnected => _session?.Connected == true;

        public event EventHandler<DeviceMessage>? MessageReceived;
        public event EventHandler<Exception>? ErrorOccurred;
        public event EventHandler<bool>? ConnectionStateChanged;

        public OpcUaProtocolAdapter(IOptions<GatewayConfiguration> config,
            ILogger<OpcUaProtocolAdapter> logger)
        {
            _config = config?.Value?.OpcUa ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            try
            {
                _logger.LogInformation("Connecting to OPC UA server: {Url}", _config.ServerUrl);

                var appConfig = await BuildApplicationConfigAsync();

                var endpointDescription = CoreClientUtils.SelectEndpoint(
                    appConfig, _config.ServerUrl, useSecurity: !_config.AcceptUntrustedCertificates);

                var endpointConfig = EndpointConfiguration.Create(appConfig);
                var endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfig);

                UserIdentity identity = string.IsNullOrWhiteSpace(_config.Username)
                    ? new UserIdentity(new AnonymousIdentityToken())
                    : new UserIdentity(_config.Username, _config.Password);

                _session = await Session.Create(
                    appConfig,
                    endpoint,
                    updateBeforeConnect: false,
                    "IoTEdgeGatewaySession",
                    (uint)_config.SessionTimeoutMs,
                    identity,
                    preferredLocales: null);

                _session.KeepAlive += OnSessionKeepAlive;
                _session.SessionClosing += OnSessionClosing;

                await SetupSubscriptionAsync(cancellationToken);

                ConnectionStateChanged?.Invoke(this, true);
                _logger.LogInformation("OPC UA connected to {Url}.", _config.ServerUrl);
            }
            catch (ServiceResultException ex)
            {
                throw new ProtocolAdapterException("OPCUA",
                    $"OPC UA connection failed to {_config.ServerUrl}: {ex.Message}", ex);
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_subscription != null)
            {
                await _subscription.DeleteItemsAsync(cancellationToken);
                _subscription.Dispose();
                _subscription = null;
            }

            if (_session != null)
            {
                _session.KeepAlive -= OnSessionKeepAlive;
                _session.SessionClosing -= OnSessionClosing;
                _session.Close();
                _session.Dispose();
                _session = null;
            }

            ConnectionStateChanged?.Invoke(this, false);
        }

        public async Task SendCommandAsync(DeviceCommand command, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_session is null || !_session.Connected)
                throw new ProtocolAdapterException("OPCUA", "Not connected.");

            // Map command to OPC UA method call or write operation
            if (command.Parameters.TryGetValue("nodeId", out var nodeIdObj)
                && command.Parameters.TryGetValue("value", out var value))
            {
                var nodeId = NodeId.Parse(nodeIdObj.ToString()!);
                var writeValue = new WriteValue
                {
                    NodeId = nodeId,
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                };

                var response = await _session.WriteAsync(null,
                    new WriteValueCollection { writeValue }, cancellationToken);

                if (!StatusCode.IsGood(response.Results[0]))
                    throw new CommandExecutionException(command.CommandName,
                        $"OPC UA write failed: {response.Results[0]}");

                _logger.LogInformation("OPC UA write command '{Name}' succeeded on node {NodeId}.",
                    command.CommandName, nodeId);
            }
            else
            {
                throw new CommandExecutionException(command.CommandName,
                    "OPC UA command requires 'nodeId' and 'value' parameters.");
            }
        }

        private async Task SetupSubscriptionAsync(CancellationToken cancellationToken)
        {
            _subscription = new Subscription(_session!.DefaultSubscription)
            {
                PublishingInterval = _config.PublishingIntervalMs,
                PublishingEnabled = true
            };

            foreach (var nodeConf in _config.NodesToMonitor)
            {
                var item = new MonitoredItem(_subscription.DefaultItem)
                {
                    StartNodeId = NodeId.Parse(nodeConf.NodeId),
                    AttributeId = Attributes.Value,
                    DisplayName = nodeConf.DisplayName,
                    SamplingInterval = nodeConf.SamplingIntervalMs,
                    QueueSize = 10,
                    DiscardOldest = true
                };

                item.Notification += (monItem, args) =>
                    OnMonitoredItemNotification(monItem, args, nodeConf);

                _subscription.AddItem(item);
            }

            _session.AddSubscription(_subscription);
            await _subscription.CreateAsync(cancellationToken);

            _logger.LogInformation("OPC UA subscription created with {Count} monitored nodes.",
                _config.NodesToMonitor.Count);
        }

        private void OnMonitoredItemNotification(MonitoredItem item,
            MonitoredItemNotificationEventArgs args, OpcUaNodeConfig nodeConf)
        {
            try
            {
                foreach (var value in item.DequeueValues())
                {
                    if (!StatusCode.IsGood(value.StatusCode))
                    {
                        _logger.LogWarning("OPC UA bad quality on node {NodeId}: {Status}",
                            nodeConf.NodeId, value.StatusCode);
                    }

                    var rawValue = value.Value;
                    var dp = new DataPoint(
                        name: string.IsNullOrWhiteSpace(nodeConf.DisplayName)
                            ? nodeConf.NodeId : nodeConf.DisplayName,
                        value: ConvertToDouble(rawValue) ?? rawValue,
                        type: DataPointType.Unknown,
                        unit: nodeConf.Unit,
                        timestamp: value.SourceTimestamp != DateTime.MinValue
                            ? new DateTimeOffset(value.SourceTimestamp, TimeSpan.Zero)
                            : DateTimeOffset.UtcNow,
                        isValid: StatusCode.IsGood(value.StatusCode),
                        minThreshold: nodeConf.MinThreshold,
                        maxThreshold: nodeConf.MaxThreshold);

                    var metadata = new Dictionary<string, string>
                    {
                        [$"node.{nodeConf.DisplayName}.qualityCode"] = value.StatusCode.Code.ToString(),
                        [$"node.{nodeConf.DisplayName}.displayName"] = nodeConf.DisplayName,
                        [$"node.{nodeConf.DisplayName}.engineeringUnit"] = nodeConf.Unit
                    };

                    var deviceId = ExtractDeviceIdFromNodeId(nodeConf.NodeId);
                    var message = new DeviceMessage(
                        new DeviceId(deviceId),
                        ProtocolType.OpcUa,
                        new[] { dp },
                        rawPayload: JsonSerializer.Serialize(rawValue),
                        metadata: metadata);

                    MessageReceived?.Invoke(this, message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling OPC UA notification for node {NodeId}.", nodeConf.NodeId);
                ErrorOccurred?.Invoke(this, ex);
            }
        }

        private static double? ConvertToDouble(object? value) => value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            ushort us => us,
            uint ui => ui,
            byte b => b,
            sbyte sb => sb,
            _ => null
        };

        private static string ExtractDeviceIdFromNodeId(string nodeId)
        {
            // ns=2;s=Device1.Temperature → Device1
            var parts = nodeId.Split(';');
            foreach (var part in parts)
            {
                if (part.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = part[2..].Split('.');
                    if (segments.Length > 0) return segments[0];
                }
            }
            return "opcua-device";
        }

        private void OnSessionKeepAlive(ISession session, KeepAliveEventArgs args)
        {
            if (ServiceResult.IsBad(args.Status))
            {
                _logger.LogWarning("OPC UA keep-alive bad status: {Status}", args.Status);
                ErrorOccurred?.Invoke(this, new ProtocolAdapterException("OPCUA",
                    $"Keep-alive failed: {args.Status}"));
            }
        }

        private void OnSessionClosing(object sender, EventArgs args)
        {
            _logger.LogWarning("OPC UA session closing.");
            ConnectionStateChanged?.Invoke(this, false);
        }

        private static async Task<ApplicationConfiguration> BuildApplicationConfigAsync()
        {
            var config = new ApplicationConfiguration
            {
                ApplicationName = "IoTEdgeGateway",
                ApplicationUri = "urn:IoTEdgeGateway",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier(),
                    AutoAcceptUntrustedCertificates = false
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
            };

            await config.Validate(ApplicationType.Client);
            return config;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpcUaProtocolAdapter));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await DisconnectAsync();
        }
    }
}
