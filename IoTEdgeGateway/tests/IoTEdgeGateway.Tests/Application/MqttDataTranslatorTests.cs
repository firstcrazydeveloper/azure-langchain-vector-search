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
    public class MqttDataTranslatorTests
    {
        private readonly MqttDataTranslator _translator;

        public MqttDataTranslatorTests()
        {
            _translator = new MqttDataTranslator(NullLogger<MqttDataTranslator>.Instance);
        }

        private static DeviceMessage MakeMessage(string payload) =>
            new(new DeviceId("device-1"), ProtocolType.Mqtt,
                Array.Empty<DataPoint>(), rawPayload: payload);

        [Fact]
        public void SupportedProtocol_ReturnsMqtt()
        {
            _translator.SupportedProtocol.Should().Be(ProtocolType.Mqtt);
        }

        [Fact]
        public void Translate_FlatJson_ExtractsAllFields()
        {
            var json = """{"temperature":85.5,"humidity":60.0,"pressure":101.3}""";
            var result = _translator.Translate(MakeMessage(json)).ToList();

            result.Should().HaveCount(3);
            result.Should().Contain(dp => dp.Name == "temperature" && (double)dp.Value! == 85.5);
            result.Should().Contain(dp => dp.Name == "humidity");
            result.Should().Contain(dp => dp.Name == "pressure");
        }

        [Fact]
        public void Translate_NestedJson_UsesDotsForNestedKeys()
        {
            var json = """{"motor":{"speed":1500,"current":12.3}}""";
            var result = _translator.Translate(MakeMessage(json)).ToList();

            result.Should().Contain(dp => dp.Name == "motor.speed");
            result.Should().Contain(dp => dp.Name == "motor.current");
        }

        [Fact]
        public void Translate_EmptyPayload_ReturnsEmpty()
        {
            var result = _translator.Translate(MakeMessage("")).ToList();
            result.Should().BeEmpty();
        }

        [Fact]
        public void Translate_InvalidJson_ReturnsRawPayloadDataPoint()
        {
            var result = _translator.Translate(MakeMessage("not json")).ToList();
            result.Should().HaveCount(1);
            result[0].Name.Should().Be("raw_payload");
            result[0].IsValid.Should().BeFalse();
        }

        [Fact]
        public void Translate_JsonWithBoolean_CreatesBooleanDataPoint()
        {
            var json = """{"active":true}""";
            var result = _translator.Translate(MakeMessage(json)).ToList();

            result.Should().HaveCount(1);
            result[0].Type.Should().Be(DataPointType.Boolean);
            result[0].Value.Should().Be(true);
        }

        [Fact]
        public void Translate_JsonWithString_CreatesStringDataPoint()
        {
            var json = """{"status":"running"}""";
            var result = _translator.Translate(MakeMessage(json)).ToList();

            result[0].Type.Should().Be(DataPointType.String);
            result[0].Value.Should().Be("running");
        }

        [Fact]
        public void Translate_TemperatureField_InfersType()
        {
            var json = """{"temperature_sensor":95.0}""";
            var result = _translator.Translate(MakeMessage(json)).ToList();

            result[0].Type.Should().Be(DataPointType.Temperature);
            result[0].Unit.Should().Be("°C");
        }

        [Fact]
        public void Translate_NullMessage_ThrowsArgumentNullException()
        {
            Action act = () => _translator.Translate(null!).ToList();
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Translate_JsonArray_IndexesElements()
        {
            var json = """{"readings":[10.0,20.0,30.0]}""";
            var result = _translator.Translate(MakeMessage(json)).ToList();

            result.Should().Contain(dp => dp.Name == "readings[0]");
            result.Should().Contain(dp => dp.Name == "readings[1]");
            result.Should().Contain(dp => dp.Name == "readings[2]");
        }

        [Fact]
        public void Translate_NullJsonValue_SkipsField()
        {
            var json = """{"temperature":null,"pressure":1.0}""";
            var result = _translator.Translate(MakeMessage(json)).ToList();
            // null JSON values produce no DataPoints (not a number/string/bool)
            result.Should().Contain(dp => dp.Name == "pressure");
        }
    }
}
