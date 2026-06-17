using System.Buffers;

using AwesomeAssertions;

using BareWire.Buffers;
using BareWire.Serialization.MsgPack;

namespace BareWire.UnitTests.Serialization;

public sealed class MessagePackSerializerTests
{
    private readonly MessagePackSerializer _sut = new();
    private readonly MessagePackDeserializer _deserializer = new();

    [Fact]
    public void ContentType_ReturnsApplicationXMsgpack()
    {
        _sut.ContentType.Should().Be("application/x-msgpack");
    }

    [Fact]
    public void Serialize_SimpleRecord_RoundTripsThroughDeserializer()
    {
        using var writer = new PooledBufferWriter();
        var original = new MsgPackSimpleMessage("hello", 42);

        _sut.Serialize(original, writer);
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
        var result = _deserializer.Deserialize<MsgPackSimpleMessage>(sequence);

        result.Should().NotBeNull();
        result!.Name.Should().Be("hello");
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Serialize_NestedObject_RoundTrips()
    {
        using var writer = new PooledBufferWriter();
        var original = new MsgPackNestedMessage("outer", new MsgPackInnerData(7, "inner-desc"));

        _sut.Serialize(original, writer);
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
        var result = _deserializer.Deserialize<MsgPackNestedMessage>(sequence);

        result.Should().NotBeNull();
        result!.Label.Should().Be("outer");
        result.Inner.Id.Should().Be(7);
        result.Inner.Description.Should().Be("inner-desc");
    }

    [Fact]
    public void Serialize_RecordWithCollections_RoundTrips()
    {
        using var writer = new PooledBufferWriter();
        var original = new MsgPackCollectionMessage(
            "tagged",
            ["a", "b", "c"],
            new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 });

        _sut.Serialize(original, writer);
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
        var result = _deserializer.Deserialize<MsgPackCollectionMessage>(sequence);

        result.Should().NotBeNull();
        result!.Name.Should().Be("tagged");
        result.Tags.Should().BeEquivalentTo(["a", "b", "c"]);
        result.Scores["x"].Should().Be(1);
        result.Scores["y"].Should().Be(2);
    }

    [Fact]
    public void Serialize_NullMessage_ThrowsArgumentNullException()
    {
        using var writer = new PooledBufferWriter();
        MsgPackSimpleMessage message = null!;

        Action act = () => _sut.Serialize(message, writer);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("message");
    }

    [Fact]
    public void Serialize_NullOutput_ThrowsArgumentNullException()
    {
        IBufferWriter<byte> output = null!;
        var message = new MsgPackSimpleMessage("x", 1);

        Action act = () => _sut.Serialize(message, output);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("output");
    }
}

public sealed record MsgPackSimpleMessage(string Name, int Value);
public sealed record MsgPackNestedMessage(string Label, MsgPackInnerData Inner);
public sealed record MsgPackInnerData(int Id, string Description);
public sealed record MsgPackCollectionMessage(string Name, List<string> Tags, Dictionary<string, int> Scores);
