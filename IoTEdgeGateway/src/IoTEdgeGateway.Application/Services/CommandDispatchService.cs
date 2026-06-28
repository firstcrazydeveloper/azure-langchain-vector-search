using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace IoTEdgeGateway.Application.Services
{
    public sealed class CommandDispatchService : IAsyncDisposable
    {
        private readonly IEnumerable<ICommandHandler> _handlers;
        private readonly IIoTHubClient _iotHubClient;
        private readonly IEnumerable<IProtocolAdapter> _adapters;
        private readonly ILogger<CommandDispatchService> _logger;

        // Track in-flight commands for timeout enforcement
        private readonly ConcurrentDictionary<Guid, DeviceCommand> _pendingCommands = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _timeoutWatcher;

        public CommandDispatchService(
            IEnumerable<ICommandHandler> handlers,
            IIoTHubClient iotHubClient,
            IEnumerable<IProtocolAdapter> adapters,
            ILogger<CommandDispatchService> logger)
        {
            _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
            _iotHubClient = iotHubClient ?? throw new ArgumentNullException(nameof(iotHubClient));
            _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _iotHubClient.CommandReceived += OnCloudCommandReceived;
            _timeoutWatcher = WatchTimeoutsAsync(_cts.Token);
        }

        private async void OnCloudCommandReceived(object? sender, DeviceCommand command)
        {
            try
            {
                await DispatchAsync(command, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error dispatching command {CommandId}.", command.CommandId);
            }
        }

        public async Task DispatchAsync(DeviceCommand command, CancellationToken cancellationToken = default)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));

            _logger.LogInformation("Dispatching command '{Name}' to device {DeviceId} [id={CommandId}].",
                command.CommandName, command.TargetDeviceId, command.CommandId);

            if (command.IsExpired())
            {
                command.MarkTimedOut();
                _logger.LogWarning("Command {CommandId} was expired before dispatch.", command.CommandId);
                return;
            }

            // Try application-layer handlers first (e.g., configuration updates, local resets)
            var handler = _handlers.FirstOrDefault(h => h.CanHandle(command.CommandName));
            if (handler is not null)
            {
                await ExecuteWithHandlerAsync(command, handler, cancellationToken);
                return;
            }

            // Fall back to protocol-level command forwarding
            await ForwardToProtocolAdapterAsync(command, cancellationToken);
        }

        private async Task ExecuteWithHandlerAsync(DeviceCommand command, ICommandHandler handler,
            CancellationToken cancellationToken)
        {
            command.MarkExecuting();
            _pendingCommands[command.CommandId] = command;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(command.Timeout);

                var success = await handler.HandleAsync(command, timeout.Token);
                if (success)
                {
                    command.MarkSucceeded();
                    _logger.LogInformation("Command {CommandId} '{Name}' succeeded.", command.CommandId, command.CommandName);
                }
                else
                {
                    command.MarkFailed("Handler returned failure.");
                    _logger.LogWarning("Command {CommandId} '{Name}' failed.", command.CommandId, command.CommandName);
                }
            }
            catch (OperationCanceledException)
            {
                command.MarkTimedOut();
                _logger.LogWarning("Command {CommandId} timed out.", command.CommandId);
            }
            catch (Exception ex)
            {
                command.MarkFailed(ex.Message);
                _logger.LogError(ex, "Command {CommandId} threw exception.", command.CommandId);
            }
            finally
            {
                _pendingCommands.TryRemove(command.CommandId, out _);
            }
        }

        private async Task ForwardToProtocolAdapterAsync(DeviceCommand command,
            CancellationToken cancellationToken)
        {
            var adapter = _adapters.FirstOrDefault(a => a.IsConnected);
            if (adapter is null)
            {
                command.MarkRejected("No connected protocol adapter available.");
                _logger.LogError("No adapter available for command {CommandId}.", command.CommandId);
                return;
            }

            command.MarkExecuting();
            _pendingCommands[command.CommandId] = command;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(command.Timeout);

                await adapter.SendCommandAsync(command, timeout.Token);
                command.MarkSucceeded();
            }
            catch (OperationCanceledException)
            {
                command.MarkTimedOut();
            }
            catch (Exception ex)
            {
                command.MarkFailed(ex.Message);
                _logger.LogError(ex, "Protocol adapter failed for command {CommandId}.", command.CommandId);
            }
            finally
            {
                _pendingCommands.TryRemove(command.CommandId, out _);
            }
        }

        private async Task WatchTimeoutsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

                foreach (var kvp in _pendingCommands)
                {
                    if (kvp.Value.IsExpired())
                    {
                        kvp.Value.MarkTimedOut();
                        _pendingCommands.TryRemove(kvp.Key, out _);
                        _logger.LogWarning("Command {CommandId} expired during execution.", kvp.Key);
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _iotHubClient.CommandReceived -= OnCloudCommandReceived;
            await _cts.CancelAsync();
            try { await _timeoutWatcher; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }
}
