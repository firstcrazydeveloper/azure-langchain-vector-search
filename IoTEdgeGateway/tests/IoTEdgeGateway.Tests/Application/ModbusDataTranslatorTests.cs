using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using IoTEdgeGateway.Application.Translators;
using IoTEdgeGateway.Domain.Entities;
using IoTEdgeGateway.Domain.Enums;
using IoTEdgeGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IoTEdgeGateway.Tests.Application
{
    public class ModbusDataTranslatorTests
    {
        private readonly ModbusDataTranslator _translator =
            new(NullLogger<ModbusDataTranslator>.Instance);

        [Fact]
        public void SupportedProtocol_ReturnsModbusTcp()
        {
            _translator.SupportedProtocol.Should().Be(ProtocolType.ModbusTcp);
        }

        [Fact]
        public void Translate_AppliesScaleAndOffset()
        {
            var dp = new DataPoint("register_40001", 1500.0, DataPointType.Unknown);
            var metadata = new Dictionary<string, string>
            {
                ["register.register_40001.name"] = "motor_speed",
                ["register.register_40001.scale"] = "0.1",
                ["register.register_40001.offset"] = "0",
                ["register.register_40001.unit"] = "RPM",
                ["register.register_40001.type"] = "speed"
            };

            var message = new DeviceMessage(
                new DeviceId("modbus-1"), ProtocolType.ModbusTcp,
                new[] { dp }, metadata: metadata);

            var result = _translator.Translate(message).ToList();

            result.Should().HaveCount(1);
            result[0].Name.Should().Be("motor_speed");
            result[0].AsDouble().Should().BeApproximately(150.0, 0.001); // 1500 * 0.1
            result[0].Unit.Should().Be("RPM");
            result[0].Type.Should().Be(DataPointType.Speed);
        }

        [Fact]
        public void Translate_ZeroScale_SkipsScaling()
        {
            var dp = new DataPoint("register_40002", 100.0, DataPointType.Unknown);
            var metadata = new Dictionary<string, string>
            {
                ["register.register_40002.scale"] = "0"
            };

            var message = new DeviceMessage(
                new DeviceId("modbus-1"), ProtocolType.ModbusTcp,
                new[] { dp }, metadata: metadata);

            var result = _translator.Translate(message).ToList();
            // Zero scale is ignored to avoid divide-by-zero or zeroing all values
            result[0].AsDouble().Should().Be(100.0);
        }

        [Fact]
        public void Translate_AppliesThresholdsFromMetadata()
        {
            var dp = new DataPoint("register_40003", 50.0, DataPointType.Unknown);
            var metadata = new Dictionary<string, string>
            {
                ["register.register_40003.min"] = "0",
                ["register.register_40003.max"] = "100",
                ["register.register_40003.scale"] = "1.0"
            };

            var message = new DeviceMessage(
                new DeviceId("m"), ProtocolType.ModbusTcp, new[] { dp }, metadata: metadata);

            var result = _translator.Translate(message).ToList();
            result[0].MinThreshold.Should().Be(0.0);
            result[0].MaxThreshold.Should().Be(100.0);
        }

        [Fact]
        public void Translate_NullMessage_Throws()
        {
            Action act = () => _translator.Translate(null!).ToList();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Translate_NoMetadata_PreservesOriginalValues()
        {
            var dp = new DataPoint("register_40004", 42.0, DataPointType.Pressure);
            var message = new DeviceMessage(
                new DeviceId("m"), ProtocolType.ModbusTcp, new[] { dp });

            var result = _translator.Translate(message).ToList();
            result[0].Name.Should().Be("register_40004");
            result[0].AsDouble().Should().Be(42.0);
        }
    }
}
