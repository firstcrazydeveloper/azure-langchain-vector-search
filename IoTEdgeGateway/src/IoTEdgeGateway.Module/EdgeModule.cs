using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Application.Pipeline;
using IoTEdgeGateway.Application.Services;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoTEdgeGateway.Module
{
    public sealed class EdgeModule : BackgroundService
    {
        private readonly IEnumerable<IProtocolAdapter> _adapters;
        private readonly DataProcessingPipeline _pipeline;
        private readonly CommandDispatchService _commandDispatch;
        private readonly IIoTHubClient _iotHubClient;
        private readonly ILogger<EdgeModule> _logger;

        public EdgeModule(
            IEnumerable<IProtocolAdapter> adapters,
            DataProcessingPipeline pipeline,
            CommandDispatchService commandDispatch,
            IIoTHubClient iotHubClient,
            ILogger<EdgeModule> logger)
        {
            _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
            _iotHubClient = iotHubClient ?? throw new ArgumentNullException(nameof(iotHubClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("IoT Edge Gateway Module starting...");

            // Connect to IoT Hub first
            await _iotHubClient.ConnectAsync(stoppingToken);

            // Connect all protocol adapters and hook up message processing
            foreach (var adapter in _adapters)
            {
                adapter.MessageReceived += OnAdapterMessageReceived;
                adapter.ErrorOccurred += OnAdapterError;
                adapter.ConnectionStateChanged += OnConnectionStateChanged;

                try
                {
                    await adapter.ConnectAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    // Log but continue — other adapters should still work
                    _logger.LogError(ex, "Failed to connect {Protocol} adapter. Continuing with others.",
                        adapter.Protocol);
                }
            }

            _logger.LogInformation("IoT Edge Gateway Module running. Waiting for cancellation...");

            // Keep alive until host shutdown
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("IoT Edge Gateway Module stopping...");

            foreach (var adapter in _adapters)
            {
                adapter.MessageReceived -= OnAdapterMessageReceived;
                adapter.ErrorOccurred -= OnAdapterError;
                adapter.ConnectionStateChanged -= OnConnectionStateChanged;

                try { await adapter.DisconnectAsync(cancellationToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting {Protocol}.", adapter.Protocol); }
            }

            await _iotHubClient.DisconnectAsync(cancellationToken);
            await _commandDispatch.DisposeAsync();
            await base.StopAsync(cancellationToken);
        }

        private async void OnAdapterMessageReceived(object? sender, DeviceMessage message)
        {
            try
            {
                await _pipeline.ProcessAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in pipeline for device {DeviceId}.", message.DeviceId);
            }
        }

        private void OnAdapterError(object? sender, Exception ex)
        {
            _logger.LogError(ex, "Protocol adapter error from {Adapter}.", sender?.GetType().Name);
        }

        private void OnConnectionStateChanged(object? sender, bool isConnected)
        {
            _logger.LogInformation("Adapter {Adapter} connection: {State}",
                sender?.GetType().Name, isConnected ? "Connected" : "Disconnected");
        }
    }
}
