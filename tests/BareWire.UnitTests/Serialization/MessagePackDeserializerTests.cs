using System.Buffers;

using AwesomeAssertions;

using BareWire.Abstractions.Exceptions;
using BareWire.Buffers;
using BareWire.Serialization.MsgPack;

namespace BareWire.UnitTests.Serialization;

public sealed class MessagePackDeserializerTests
{
    private readonly MessagePackSerializer _serializer = new();
    private readonly MessagePackDeserializer _sut = new();

    [Fact]
    public void ContentType_ReturnsApplicationXMsgpack()
    {
        _sut.ContentType.Should().Be("application/x-msgpack");
    }

    [Fact]
    public void Deserialize_EmptySequence_ReturnsNull()
    {
        var result = _sut.Deserialize<MsgPackSimpleMessage>(ReadOnlySequence<byte>.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public void Deserialize_RoundTripFromSerializer_ReturnsEquivalent()
    {
        var original = new MsgPackSimpleMessage("world", 99);
        using var writer = new PooledBufferWriter();
        _serializer.Serialize(original, writer);
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());

        var result = _sut.Deserialize<MsgPackSimpleMessage>(sequence);

        result.Should().NotBeNull();
        result!.Name.Should().Be("world");
        result.Value.Should().Be(99);
    }

    [Fact]
    public void Deserialize_MultiSegmentSequence_RoundTrips()
    {
        // Serialize to a contiguous buffer, then deliberately split it into >=2 segments
        // to exercise the zero-copy consume path via MessagePackReader(ReadOnlySequence<byte>).
        var original = new MsgPackSimpleMessage("multi", 7);
        using var writer = new PooledBufferWriter();
        _serializer.Serialize(original, writer);
        byte[] bytes = writer.WrittenMemory.ToArray();

        // Guarantee at least 2 segments by splitting at the midpoint.
        var sequence = CreateMultiSegmentSequence(bytes, segmentSize: Math.Max(1, bytes.Length / 2));

        sequence.IsSingleSegment.Should().BeFalse("test requires a genuinely multi-segment sequence");

        var result = _sut.Deserialize<MsgPackSimpleMessage>(sequence);

        result.Should().NotBeNull();
        result!.Name.Should().Be("multi");
        result.Value.Should().Be(7);
    }

    [Fact]
    public void Deserialize_CorruptPayload_ThrowsBareWireSerializationException()
    {
        // 0xFF bytes are not valid MessagePack and should trigger a deserialization error.
        var corrupt = new ReadOnlySequence<byte>(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

        Action act = () => _sut.Deserialize<MsgPackSimpleMessage>(corrupt);

        var ex = act.Should().Throw<BareWireSerializationException>().Which;
        ex.ContentType.Should().Be("application/x-msgpack");
        ex.TargetType.Should().Be<MsgPackSimpleMessage>();
    }

    // --- Multi-segment helper ---

    private static ReadOnlySequence<byte> CreateMultiSegmentSequence(byte[] data, int segmentSize)
    {
        if (data.Length <= segmentSize)
        {
            // Force two segments even for tiny payloads
            segmentSize = Math.Max(1, data.Length / 2);
        }

        var segments = new List<MsgPackTestSegment>();
        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            int length = Math.Min(segmentSize, data.Length - offset);
            segments.Add(new MsgPackTestSegment(data.AsMemory(offset, length)));
        }

        // Link segments
        for (int i = 1; i < segments.Count; i++)
            segments[i - 1].SetNext(segments[i]);

        return new ReadOnlySequence<byte>(
            segments[0], 0,
            segments[^1], segments[^1].Memory.Length);
    }

    private sealed class MsgPackTestSegment : ReadOnlySequenceSegment<byte>
    {
        public MsgPackTestSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void SetNext(MsgPackTestSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
