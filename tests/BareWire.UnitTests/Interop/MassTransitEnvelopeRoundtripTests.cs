using System.Buffers;
using AwesomeAssertions;
using BareWire.Buffers;
using BareWire.Interop.MassTransit;

namespace BareWire.UnitTests.Interop;

public sealed class MassTransitEnvelopeRoundtripTests
{
    private readonly MassTransitEnvelopeSerializer _serializer = new();
    private readonly MassTransitEnvelopeDeserializer _deserializer = new();

    [Fact]
    public void SerializeThenDeserialize_PreservesMessageFields()
    {
        var original = new TestOrder("ORD-ROUND", 42.50m);
        using var buffer = new PooledBufferWriter();

        _serializer.Serialize(original, buffer);

        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var result = _deserializer.Deserialize<TestOrder>(sequence);

        result.Should().NotBeNull();
        result!.OrderId.Should().Be("ORD-ROUND");
        result.Amount.Should().Be(42.50m);
    }
}
