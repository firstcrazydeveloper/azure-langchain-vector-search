using System;
using System.Collections.Generic;
using System.Net.Sockets;
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
using NModbus;

namespace IoTEdgeGateway.Infrastructure.Adapters
{
    public sealed class ModbusTcpProtocolAdapter : IProtocolAdapter
    {
        private readonly ModbusAdapterConfig _config;
        private readonly ILogger<ModbusTcpProtocolAdapter> _logger;
        private TcpClient? _tcpClient;
        private IModbusMaster? _master;
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private bool _connected;
        private bool _disposed;

        public ProtocolType Protocol => ProtocolType.ModbusTcp;
        public bool IsConnected => _connected && _tcpClient?.Connected == true;

        public event EventHandler<DeviceMessage>? MessageReceived;
        public event EventHandler<Exception>? ErrorOccurred;
        public event EventHandler<bool>? ConnectionStateChanged;

        public ModbusTcpProtocolAdapter(IOptions<GatewayConfiguration> config,
            ILogger<ModbusTcpProtocolAdapter> logger)
        {
            _config = config?.Value?.Modbus ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            try
            {
                _logger.LogInformation("Connecting Modbus TCP to {Host}:{Port}...",
                    _config.Host, _config.Port);

                _tcpClient = new TcpClient();
                _tcpClient.ReceiveTimeout = _config.TimeoutMs;
                _tcpClient.SendTimeout = _config.TimeoutMs;

                await _tcpClient.ConnectAsync(_config.Host, _config.Port, cancellationToken);

                var factory = new ModbusFactory();
                _master = factory.CreateMaster(_tcpClient);
                _master.Transport!.ReadTimeout = _config.TimeoutMs;
                _master.Transport.Retries = _config.MaxRetries;

                _connected = true;
                ConnectionStateChanged?.Invoke(this, true);

                _logger.LogInformation("Modbus TCP connected to {Host}:{Port}.", _config.Host, _config.Port);

                // Start polling loop
                _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _pollTask = PollRegistersAsync(_pollCts.Token);
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                throw new ProtocolAdapterException("MODBUS",
                    $"Failed to connect to {_config.Host}:{_config.Port}", ex);
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;

            if (_pollCts is not null)
            {
                await _pollCts.CancelAsync();
                try { if (_pollTask is not null) await _pollTask; }
                catch (OperationCanceledException) { }
                _pollCts.Dispose();
                _pollCts = null;
            }

            _master?.Dispose();
            _master = null;
            _tcpClient?.Close();
            _tcpClient?.Dispose();
            _tcpClient = null;

            ConnectionStateChanged?.Invoke(this, false);
        }

        public async Task SendCommandAsync(DeviceCommand command, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_master is null || !IsConnected)
                throw new ProtocolAdapterException("MODBUS", "Not connected.");

            if (!command.Parameters.TryGetValue("address", out var addrObj)
                || !ushort.TryParse(addrObj.ToString(), out var address))
                throw new CommandExecutionException(command.CommandName, "Missing 'address' parameter.");

            if (!command.Parameters.TryGetValue("value", out var valueObj))
                throw new CommandExecutionException(command.CommandName, "Missing 'value' parameter.");

            var registerType = command.Parameters.TryGetValue("registerType", out var rt)
                ? rt.ToString() : "holding";

            try
            {
                switch (registerType?.ToLowerInvariant())
                {
                    case "coil":
                        var coilVal = Convert.ToBoolean(valueObj);
                        await _master.WriteSingleCoilAsync(_config.SlaveId, address, coilVal);
                        break;

                    case "holding":
                    default:
                        var regVal = Convert.ToUInt16(valueObj);
                        await _master.WriteSingleRegisterAsync(_config.SlaveId, address, regVal);
                        break;
                }

                _logger.LogInformation("Modbus write: address={Address} value={Value} type={Type}.",
                    address, valueObj, registerType);
            }
            catch (Exception ex) when (ex is not CommandExecutionException)
            {
                throw new CommandExecutionException(command.CommandName,
                    $"Modbus write failed at address {address}: {ex.Message}", ex);
            }
        }

        private async Task PollRegistersAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Modbus polling started with {Count} registers every {Interval}ms.",
                _config.Registers.Count, _config.PollingIntervalMs);

            var deviceId = $"modbus-{_config.Host}:{_config.Port}";

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.PollingIntervalMs, cancellationToken);

                    var dataPoints = new List<DataPoint>();
                    var metadata = new Dictionary<string, string>();

                    foreach (var reg in _config.Registers)
                    {
                        var dp = await ReadRegisterAsync(reg, cancellationToken);
                        if (dp is not null)
                        {
                            dataPoints.Add(dp);
                            metadata[$"register.register_{reg.Address}.name"] = reg.Name;
                            metadata[$"register.register_{reg.Address}.scale"] = reg.Scale.ToString();
                            metadata[$"register.register_{reg.Address}.offset"] = reg.Offset.ToString();
                            metadata[$"register.register_{reg.Address}.unit"] = reg.Unit;
                            metadata[$"register.register_{reg.Address}.type"] = reg.Type;
                            if (reg.MinThreshold.HasValue)
                                metadata[$"register.register_{reg.Address}.min"] = reg.MinThreshold.Value.ToString();
                            if (reg.MaxThreshold.HasValue)
                                metadata[$"register.register_{reg.Address}.max"] = reg.MaxThreshold.Value.ToString();
                        }
                    }

                    if (dataPoints.Count > 0)
                    {
                        var message = new DeviceMessage(
                            new DeviceId(deviceId),
                            ProtocolType.ModbusTcp,
                            dataPoints,
                            metadata: metadata);

                        MessageReceived?.Invoke(this, message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Modbus poll error.");
                    ErrorOccurred?.Invoke(this, ex);

                    // Brief backoff on error to avoid hammering a faulty device
                    try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _logger.LogInformation("Modbus polling stopped.");
        }

        private async Task<DataPoint?> ReadRegisterAsync(ModbusRegisterConfig reg,
            CancellationToken cancellationToken)
        {
            try
            {
                double rawValue;

                switch (reg.Type.ToLowerInvariant())
                {
                    case "coil":
                        var coils = await _master!.ReadCoilsAsync(_config.SlaveId, reg.Address, 1);
                        rawValue = coils[0] ? 1.0 : 0.0;
                        break;

                    case "discrete":
                        var discretes = await _master!.ReadInputsAsync(_config.SlaveId, reg.Address, 1);
                        rawValue = discretes[0] ? 1.0 : 0.0;
                        break;

                    case "input":
                        var inputRegs = await _master!.ReadInputRegistersAsync(_config.SlaveId, reg.Address, 1);
                        rawValue = inputRegs[0];
                        break;

                    case "float32":
                        var floatRegs = await _master!.ReadHoldingRegistersAsync(_config.SlaveId, reg.Address, 2);
                        rawValue = ConvertRegistersToFloat(floatRegs);
                        break;

                    case "holding":
                    default:
                        var holdingRegs = await _master!.ReadHoldingRegistersAsync(_config.SlaveId, reg.Address, 1);
                        rawValue = holdingRegs[0];
                        break;
                }

                return new DataPoint(
                    $"register_{reg.Address}",
                    rawValue,
                    DataPointType.Unknown,
                    reg.Unit,
                    DateTimeOffset.UtcNow,
                    isValid: true,
                    reg.MinThreshold,
                    reg.MaxThreshold);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Modbus register {Address} ({Name}).",
                    reg.Address, reg.Name);
                return null;
            }
        }

        private static double ConvertRegistersToFloat(ushort[] registers)
        {
            // Big-endian IEEE 754 float from two 16-bit registers
            var bytes = new byte[4];
            bytes[0] = (byte)(registers[0] >> 8);
            bytes[1] = (byte)(registers[0] & 0xFF);
            bytes[2] = (byte)(registers[1] >> 8);
            bytes[3] = (byte)(registers[1] & 0xFF);
            return BitConverter.ToSingle(bytes, 0);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ModbusTcpProtocolAdapter));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await DisconnectAsync();
        }
    }
}
