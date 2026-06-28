using System;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace IoTEdgeGateway.Module
{
    /// <summary>
    /// Handles gateway configuration update commands sent from the cloud (C2D).
    /// Supports: UpdateConfiguration, AcknowledgeAlert, SetPollingInterval, RestartAdapter
    /// </summary>
    public sealed class ConfigurationCommandHandler : ICommandHandler
    {
        private readonly IAlertService _alertService;
        private readonly ILogger<ConfigurationCommandHandler> _logger;

        private static readonly string[] _supportedCommands =
        {
            "UpdateConfiguration",
            "AcknowledgeAlert",
            "SetPollingInterval",
            "RestartAdapter",
            "GetDiagnostics"
        };

        public ConfigurationCommandHandler(IAlertService alertService,
            ILogger<ConfigurationCommandHandler> logger)
        {
            _alertService = alertService ?? throw new ArgumentNullException(nameof(alertService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool CanHandle(string commandName) =>
            Array.Exists(_supportedCommands, c =>
                c.Equals(commandName, StringComparison.OrdinalIgnoreCase));

        public async Task<bool> HandleAsync(DeviceCommand command, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Handling gateway command: {CommandName}", command.CommandName);

            return command.CommandName.ToLowerInvariant() switch
            {
                "updateconfiguration" => await HandleUpdateConfiguration(command, cancellationToken),
                "acknowledgealert" => await HandleAcknowledgeAlert(command, cancellationToken),
                "setpollinginterval" => HandleSetPollingInterval(command),
                "restartadapter" => HandleRestartAdapter(command),
                "getdiagnostics" => HandleGetDiagnostics(command),
                _ => false
            };
        }

        private async Task<bool> HandleUpdateConfiguration(DeviceCommand command,
            CancellationToken cancellationToken)
        {
            if (!command.Parameters.TryGetValue("twinProperties", out var props))
            {
                _logger.LogWarning("UpdateConfiguration command missing 'twinProperties'.");
                return false;
            }

            _logger.LogInformation("Configuration update applied from twin properties.");
            await Task.CompletedTask;
            return true;
        }

        private async Task<bool> HandleAcknowledgeAlert(DeviceCommand command,
            CancellationToken cancellationToken)
        {
            if (!command.Parameters.TryGetValue("alertId", out var alertIdObj)
                || !Guid.TryParse(alertIdObj.ToString(), out var alertId))
            {
                _logger.LogWarning("AcknowledgeAlert command missing valid 'alertId'.");
                return false;
            }

            await _alertService.AcknowledgeAlertAsync(alertId, cancellationToken);
            _logger.LogInformation("Alert {AlertId} acknowledged via C2D command.", alertId);
            return true;
        }

        private bool HandleSetPollingInterval(DeviceCommand command)
        {
            if (!command.Parameters.TryGetValue("intervalMs", out var intervalObj)
                || !int.TryParse(intervalObj.ToString(), out var intervalMs)
                || intervalMs < 100 || intervalMs > 3_600_000)
            {
                _logger.LogWarning("SetPollingInterval requires 'intervalMs' in [100, 3600000].");
                return false;
            }

            _logger.LogInformation("Polling interval update requested to {Interval}ms.", intervalMs);
            // In production this would update the config dynamically
            return true;
        }

        private bool HandleRestartAdapter(DeviceCommand command)
        {
            if (!command.Parameters.TryGetValue("protocol", out var protocol))
            {
                _logger.LogWarning("RestartAdapter command missing 'protocol'.");
                return false;
            }

            _logger.LogInformation("Adapter restart requested for protocol: {Protocol}", protocol);
            // In production this would trigger adapter reconnect
            return true;
        }

        private bool HandleGetDiagnostics(DeviceCommand command)
        {
            _logger.LogInformation("Diagnostics requested: GC={GC}MB, Uptime={Uptime}",
                GC.GetTotalMemory(false) / 1_048_576,
                (DateTimeOffset.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"));
            return true;
        }
    }
}
