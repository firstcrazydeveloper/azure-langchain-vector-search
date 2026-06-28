using System;
using FluentAssertions;
using IoTEdgeGateway.Domain.ValueObjects;
using Xunit;

namespace IoTEdgeGateway.Tests.Domain
{
    public class DeviceIdTests
    {
        [Fact]
        public void Constructor_ValidId_CreatesDeviceId()
        {
            var id = new DeviceId("device-001");
            id.Value.Should().Be("device-001");
        }

        [Fact]
        public void Constructor_WhitespaceAroundId_TrimsValue()
        {
            var id = new DeviceId("  device-001  ");
            id.Value.Should().Be("device-001");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Constructor_NullOrEmpty_ThrowsArgumentException(string? value)
        {
            Action act = () => new DeviceId(value!);
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Constructor_OverMaxLength_ThrowsArgumentException()
        {
            var longId = new string('a', 129);
            Action act = () => new DeviceId(longId);
            act.Should().Throw<ArgumentException>().WithMessage("*128*");
        }

        [Fact]
        public void Equality_SameValue_AreEqual()
        {
            var id1 = new DeviceId("device-1");
            var id2 = new DeviceId("device-1");

            id1.Should().Be(id2);
            id1.Equals(id2).Should().BeTrue();
            (id1 == id2).Should().BeFalse(); // Reference types — no == overload
        }

        [Fact]
        public void Equality_DifferentValues_NotEqual()
        {
            new DeviceId("a").Should().NotBe(new DeviceId("b"));
        }

        [Fact]
        public void ImplicitConversion_ToString_ReturnsValue()
        {
            var id = new DeviceId("device-1");
            string str = id;
            str.Should().Be("device-1");
        }

        [Fact]
        public void ExplicitConversion_FromString_CreatesDeviceId()
        {
            var id = (DeviceId)"device-2";
            id.Value.Should().Be("device-2");
        }

        [Fact]
        public void ToString_ReturnsValue()
        {
            new DeviceId("device-3").ToString().Should().Be("device-3");
        }

        [Fact]
        public void GetHashCode_SameValues_ReturnsSameHash()
        {
            new DeviceId("x").GetHashCode().Should().Be(new DeviceId("x").GetHashCode());
        }
    }
}
