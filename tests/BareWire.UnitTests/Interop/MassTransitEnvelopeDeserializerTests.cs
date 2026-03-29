using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Interop.MassTransit;

namespace BareWire.UnitTests.Interop;

public sealed class MassTransitEnvelopeDeserializerTests
{
    private readonly MassTransitEnvelopeDeserializer _sut = new();

    [Fact]
    public void Deserialize_ValidEnvelope_ReturnsMessage()
    {
        var json = BuildEnvelopeJson("""{"orderId":"ORD-001","amount":99.99}""");
        var data = ToSequence(json);

        var result = _sut.Deserialize<TestOrder>(data);

        result.Should().NotBeNull();
        result!.OrderId.Should().Be("ORD-001");
        result.Amount.Should().Be(99.99m);
    }

    [Fact]
    public void Deserialize_EmptyData_ReturnsNull()
    {
        var result = _sut.Deserialize<TestOrder>(ReadOnlySequence<byte>.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public void Deserialize_InvalidJson_ThrowsBareWireSerializationException()
    {
        var data = ToSequence("not valid json {{{{");

        Action act = () => _sut.Deserialize<TestOrder>(data);

        act.Should().Throw<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/vnd.masstransit+json");
    }

    [Fact]
    public void Deserialize_InvalidJson_ExceptionContainsRawPayload()
    {
        const string badJson = "not valid json {{{{";
        var data = ToSequence(badJson);

        Action act = () => _sut.Deserialize<TestOrder>(data);

        act.Should().Throw<BareWireSerializationException>()
            .Which.RawPayload.Should().Contain(badJson);
    }

    [Fact]
    public void ContentType_ReturnsVndMasstransitJson()
    {
        _sut.ContentType.Should().Be("application/vnd.masstransit+json");
    }

    [Fact]
    public void Deserialize_AllMetadataFields_ParsesEnvelope()
    {
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var initiatorId = Guid.NewGuid();
        var sentTime = DateTimeOffset.UtcNow;
        var expirationTime = sentTime.AddMinutes(5);
        var json = $$"""
            {
              "messageId": "{{messageId}}",
              "correlationId": "{{correlationId}}",
              "conversationId": "{{conversationId}}",
              "initiatorId": "{{initiatorId}}",
              "sourceAddress": "rabbitmq://localhost/source",
              "destinationAddress": "rabbitmq://localhost/dest",
              "messageType": ["urn:message:TestOrder"],
              "sentTime": "{{sentTime:O}}",
              "expirationTime": "{{expirationTime:O}}",
              "headers": {"x-custom": "value"},
              "message": {"orderId":"ORD-META","amount":12.34}
            }
            """;
        var data = ToSequence(json);

        var result = _sut.Deserialize<TestOrder>(data);

        result.Should().NotBeNull();
        result!.OrderId.Should().Be("ORD-META");
        result.Amount.Should().Be(12.34m);
    }

    [Fact]
    public void Deserialize_MissingOptionalFields_ReturnsMessage()
    {
        var json = """{"message": {"orderId":"ORD-MIN","amount":1.00}}""";
        var data = ToSequence(json);

        var result = _sut.Deserialize<TestOrder>(data);

        result.Should().NotBeNull();
        result!.OrderId.Should().Be("ORD-MIN");
        result.Amount.Should().Be(1.00m);
    }

    [Fact]
    public void Deserialize_NestedMessage_ReturnsNestedData()
    {
        var json = BuildEnvelopeJson("""{"name":"Widget","inner":{"id":42,"description":"blue"}}""");
        var data = ToSequence(json);

        var result = _sut.Deserialize<NestedTestOrder>(data);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Widget");
        result.Inner.Should().NotBeNull();
        result.Inner.Id.Should().Be(42);
        result.Inner.Description.Should().Be("blue");
    }

    [Fact]
    public void Deserialize_NullMessageField_ReturnsNull()
    {
        var json = BuildEnvelopeJson("null");
        var data = ToSequence(json);

        var result = _sut.Deserialize<TestOrder>(data);

        result.Should().BeNull();
    }

    [Fact]
    public void Deserialize_MultiSegmentSequence_ReturnsMessage()
    {
        var json = BuildEnvelopeJson("""{"orderId":"ORD-SEG","amount":7.77}""");
        var bytes = Encoding.UTF8.GetBytes(json);
        var data = CreateMultiSegmentSequence(bytes, segmentSize: 4);

        var result = _sut.Deserialize<TestOrder>(data);

        result.Should().NotBeNull();
        result!.OrderId.Should().Be("ORD-SEG");
        result.Amount.Should().Be(7.77m);
    }

    // --- Helpers ---

    private static string BuildEnvelopeJson(string messageJson)
    {
        return $$"""
            {
              "messageId": "{{Guid.NewGuid()}}",
              "messageType": ["urn:message:TestOrder"],
              "sentTime": "{{DateTimeOffset.UtcNow:O}}",
              "message": {{messageJson}}
            }
            """;
    }

    private static ReadOnlySequence<byte> ToSequence(string json)
        => new(Encoding.UTF8.GetBytes(json));

    private static ReadOnlySequence<byte> CreateMultiSegmentSequence(byte[] data, int segmentSize)
    {
        if (data.Length <= segmentSize)
            return new ReadOnlySequence<byte>(data);

        var segments = new List<TestSegment>();
        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            int length = Math.Min(segmentSize, data.Length - offset);
            segments.Add(new TestSegment(data.AsMemory(offset, length)));
        }

        for (int i = 1; i < segments.Count; i++)
            segments[i - 1].SetNext(segments[i]);

        return new ReadOnlySequence<byte>(
            segments[0], 0,
            segments[^1], segments[^1].Memory.Length);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void SetNext(TestSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}

internal sealed record TestOrder(string OrderId, decimal Amount);

internal sealed record NestedTestOrder(string Name, InnerTestData Inner);

internal sealed record InnerTestData(int Id, string Description);
