using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Configuration;

namespace BareWire.UnitTests.Core.Configuration;

public sealed class ReceiveEndpointConfigurationTests
{
    [Fact]
    public void UseDeserializer_SetsDeserializerOverrideType()
    {
        ReceiveEndpointConfiguration sut = new("test-queue");
        sut.UseDeserializer<FakeDeserializer>();
        sut.DeserializerOverrideType.Should().BeSameAs(typeof(FakeDeserializer));
    }

    [Fact]
    public void UseSerializer_SetsSerializerOverrideType()
    {
        ReceiveEndpointConfiguration sut = new("test-queue");
        sut.UseSerializer<FakeSerializer>();
        sut.SerializerOverrideType.Should().BeSameAs(typeof(FakeSerializer));
    }

    [Fact]
    public void UseDeserializer_WhenCalledTwice_OverridesWithLastType()
    {
        ReceiveEndpointConfiguration sut = new("test-queue");
        sut.UseDeserializer<FakeDeserializer>();
        sut.UseDeserializer<AnotherFakeDeserializer>();
        sut.DeserializerOverrideType.Should().BeSameAs(typeof(AnotherFakeDeserializer));
    }

    [Fact]
    public void UseSerializer_WhenCalledTwice_OverridesWithLastType()
    {
        ReceiveEndpointConfiguration sut = new("test-queue");
        sut.UseSerializer<FakeSerializer>();
        sut.UseSerializer<AnotherFakeSerializer>();
        sut.SerializerOverrideType.Should().BeSameAs(typeof(AnotherFakeSerializer));
    }

    [Fact]
    public void DeserializerOverrideType_WhenNotSet_IsNull()
    {
        ReceiveEndpointConfiguration sut = new("test-queue");
        sut.DeserializerOverrideType.Should().BeNull();
    }

    [Fact]
    public void SerializerOverrideType_WhenNotSet_IsNull()
    {
        ReceiveEndpointConfiguration sut = new("test-queue");
        sut.SerializerOverrideType.Should().BeNull();
    }

    // ── Fake stub types ────────────────────────────────────────────────────────

    private sealed class FakeDeserializer : IMessageDeserializer
    {
        public string ContentType => "application/fake";
        public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class => null;
    }

    private sealed class AnotherFakeDeserializer : IMessageDeserializer
    {
        public string ContentType => "application/another-fake";
        public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class => null;
    }

    private sealed class FakeSerializer : IMessageSerializer
    {
        public string ContentType => "application/fake";
        public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class { }
    }

    private sealed class AnotherFakeSerializer : IMessageSerializer
    {
        public string ContentType => "application/another-fake";
        public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class { }
    }
}
