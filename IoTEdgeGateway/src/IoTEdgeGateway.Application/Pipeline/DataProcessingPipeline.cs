using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace IoTEdgeGateway.Application.Pipeline
{
    public sealed class PipelineResult
    {
        public string DeviceId { get; }
        public IReadOnlyList<DataPoint> ValidDataPoints { get; }
        public IReadOnlyList<DataPoint> InvalidDataPoints { get; }
        public IReadOnlyList<AlertEvent> GeneratedAlerts { get; }
        public bool SentToCloud { get; init; }

        public PipelineResult(string deviceId,
            IEnumerable<DataPoint> valid,
            IEnumerable<DataPoint> invalid,
            IEnumerable<AlertEvent> alerts)
        {
            DeviceId = deviceId;
            ValidDataPoints = valid.ToList();
            InvalidDataPoints = invalid.ToList();
            GeneratedAlerts = alerts.ToList();
        }
    }

    public sealed class DataProcessingPipeline
    {
        private readonly IEnumerable<IDataTranslator> _translators;
        private readonly IDataFilter _filter;
        private readonly IDataValidator _validator;
        private readonly IAlertService _alertService;
        private readonly IIoTHubClient _iotHubClient;
        private readonly ILogger<DataProcessingPipeline> _logger;

        public DataProcessingPipeline(
            IEnumerable<IDataTranslator> translators,
            IDataFilter filter,
            IDataValidator validator,
            IAlertService alertService,
            IIoTHubClient iotHubClient,
            ILogger<DataProcessingPipeline> logger)
        {
            _translators = translators ?? throw new ArgumentNullException(nameof(translators));
            _filter = filter ?? throw new ArgumentNullException(nameof(filter));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _alertService = alertService ?? throw new ArgumentNullException(nameof(alertService));
            _iotHubClient = iotHubClient ?? throw new ArgumentNullException(nameof(iotHubClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<PipelineResult> ProcessAsync(DeviceMessage message,
            CancellationToken cancellationToken = default)
        {
            if (message is null) throw new ArgumentNullException(nameof(message));

            _logger.LogInformation("Pipeline START: device={DeviceId} protocol={Protocol} msgId={MsgId}",
                message.DeviceId, message.Protocol, message.MessageId);

            // Step 1: Translate raw protocol data into domain DataPoints
            var translated = TranslateMessage(message);
            _logger.LogDebug("Translation: {Count} data points extracted.", translated.Count);

            // Step 2: Filter — keep only relevant/important data points
            var filtered = _filter.Filter(translated, message.DeviceId.Value).ToList();
            _logger.LogDebug("Filter: {Count}/{Total} data points passed.", filtered.Count, translated.Count);

            // Step 3: Validate — check physics ranges, thresholds, and data quality
            var valid = new List<DataPoint>();
            var invalid = new List<DataPoint>();
            var alerts = new List<AlertEvent>();

            foreach (var dp in filtered)
            {
                var result = _validator.Validate(dp);
                if (result.IsValid)
                {
                    valid.Add(dp);
                }
                else
                {
                    invalid.Add(dp);
                    _logger.LogWarning("Invalid data point '{Name}': {Errors}",
                        dp.Name, string.Join(", ", result.Errors));
                }

                // Threshold alerts are raised even for otherwise-valid points
                if (dp.ExceedsThreshold())
                {
                    var alert = BuildThresholdAlert(message.DeviceId, dp);
                    alerts.Add(alert);
                }
            }

            // Step 4: Local alert evaluation (cross-point rules, validation failures)
            await _alertService.EvaluateDataPointsAsync(message.DeviceId, valid, cancellationToken);

            foreach (var alert in alerts)
                await _alertService.RaiseAlertAsync(alert, cancellationToken);

            // Step 5: Send valid data to IoT Hub
            var sentToCloud = false;
            if (valid.Count > 0)
            {
                try
                {
                    await _iotHubClient.SendTelemetryAsync(
                        message.DeviceId.Value, valid, cancellationToken);
                    sentToCloud = true;
                    _logger.LogInformation("Pipeline COMPLETE: {Count} points sent to IoT Hub.", valid.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send telemetry to IoT Hub for device {DeviceId}.",
                        message.DeviceId);
                    // Do not re-throw: partial success still yields local results
                }
            }

            return new PipelineResult(message.DeviceId.Value, valid, invalid, alerts)
            {
                SentToCloud = sentToCloud
            };
        }

        private List<DataPoint> TranslateMessage(DeviceMessage message)
        {
            var translator = _translators
                .FirstOrDefault(t => t.SupportedProtocol == message.Protocol);

            if (translator is null)
            {
                _logger.LogWarning("No translator for protocol {Protocol}. Using raw data points.",
                    message.Protocol);
                return message.DataPoints.ToList();
            }

            try
            {
                return translator.Translate(message).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Translation failed for protocol {Protocol}.", message.Protocol);
                return new List<DataPoint>();
            }
        }

        private static AlertEvent BuildThresholdAlert(
            Domain.ValueObjects.DeviceId deviceId, DataPoint dp)
        {
            var numVal = dp.AsDouble();
            double? breachedThreshold = null;
            string direction = string.Empty;

            if (dp.MaxThreshold.HasValue && numVal > dp.MaxThreshold.Value)
            {
                breachedThreshold = dp.MaxThreshold;
                direction = "above maximum";
            }
            else if (dp.MinThreshold.HasValue && numVal < dp.MinThreshold.Value)
            {
                breachedThreshold = dp.MinThreshold;
                direction = "below minimum";
            }

            var severity = dp.Type switch
            {
                DataPointType.Temperature or DataPointType.Pressure => AlertSeverity.Critical,
                DataPointType.Vibration or DataPointType.Current => AlertSeverity.Warning,
                _ => AlertSeverity.Warning
            };

            return new AlertEvent(
                deviceId,
                severity,
                $"{dp.Name} is {direction}: value={numVal} {dp.Unit}, threshold={breachedThreshold}",
                dp.Name,
                numVal,
                breachedThreshold);
        }
    }
}
