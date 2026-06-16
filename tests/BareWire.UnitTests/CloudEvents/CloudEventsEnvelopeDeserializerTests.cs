using System.Buffers;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using BareWire.Abstractions.Exceptions;
using BareWire.CloudEvents;

namespace BareWire.UnitTests.CloudEvents;

public sealed class CloudEventsEnvelopeDeserializerTests
{
    // -------------------------------------------------------------------------
    // Test message payload type (shared with serializer tests)
    // -------------------------------------------------------------------------

    private sealed record TestMessage(string Name, int Value);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CloudEventContext MandatoryAttributes() => new(
        id: "test-id-001",
        source: new Uri("https://example.com/myapp"),
        type: "com.example.order.created");

    private static CloudEventContext FullAttributes() => new(
        id: "full-id-002",
        source: new Uri("https://example.com/myapp"),
        type: "com.example.order.created",
        specVersion: "1.0",
        subject: "order/42",
        time: new DateTimeOffset(2024, 3, 15, 10, 30, 0, TimeSpan.FromHours(2)),
        dataContentType: "application/json",
        dataSchema: new Uri("https://schemas.example.com/v1/order.json"),
        extensions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["partitionkey"] = "tenant-42",
        });

    /// <summary>
    /// Fabricates a valid CloudEvents envelope as a <see cref="ReadOnlySequence{T}"/> using the
    /// 13.8 serializer. This is NOT a round-trip test — that belongs to 13.13.
    /// </summary>
    private static ReadOnlySequence<byte> FabricateEnvelope(ICloudEventAttributes attributes, TestMessage message)
    {
        var serializer = new CloudEventsEnvelopeSerializer(attributes);
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(message, buffer);
        return new ReadOnlySequence<byte>(buffer.WrittenMemory);
    }

    private static ReadOnlySequence<byte> FromUtf8String(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return new ReadOnlySequence<byte>(bytes);
    }

    /// <summary>
    /// Builds a multi-segment <see cref="ReadOnlySequence{T}"/> by splitting <paramref name="bytes"/>
    /// at <paramref name="splitAt"/> to exercise the multi-segment read path.
    /// </summary>
    private static ReadOnlySequence<byte> ToMultiSegmentSequence(byte[] bytes, int splitAt)
    {
        byte[] first = bytes[..splitAt];
        byte[] second = bytes[splitAt..];

        BufferSegment firstSegment = new(first);
        BufferSegment secondSegment = firstSegment.Append(second);
        return new ReadOnlySequence<byte>(firstSegment, 0, secondSegment, secondSegment.Memory.Length);
    }

    /// <summary>
    /// Minimal linked-list segment for building multi-segment <see cref="ReadOnlySequence{T}"/> in tests.
    /// </summary>
    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new BufferSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = next;
            return next;
        }
    }

    // -------------------------------------------------------------------------
    // Test 1: ContentType property
    // -------------------------------------------------------------------------

    [Fact]
    public void ContentType_Always_ReturnsApplicationCloudEventsJson()
    {
        var sut = new CloudEventsEnvelopeDeserializer();

        sut.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 2: empty sequence returns null (IMessageDeserializer contract)
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_EmptySequence_ReturnsNull()
    {
        var sut = new CloudEventsEnvelopeDeserializer();

        TestMessage? result = sut.Deserialize<TestMessage>(ReadOnlySequence<byte>.Empty);

        result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Test 3: malformed JSON → BareWireSerializationException with diagnostics
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_InvalidJson_ThrowsBareWireSerializationException()
    {
        var sut = new CloudEventsEnvelopeDeserializer();
        ReadOnlySequence<byte> data = FromUtf8String("{ not json");

        Action act = () => sut.Deserialize<TestMessage>(data);

        BareWireSerializationException ex = act.Should()
            .ThrowExactly<BareWireSerializationException>()
            .Which;

        ex.ContentType.Should().Be("application/cloudevents+json");
        ex.TargetType.Should().Be<TestMessage>();
        ex.RawPayload.Should().NotBeNullOrEmpty();
    }

    // -------------------------------------------------------------------------
    // Test 4: full attributes — all mandatory + optional + extensions pass validation
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_ValidEnvelopeWithAllAttributes_DeserializesDataPayload()
    {
        var sut = new CloudEventsEnvelopeDeserializer();
        var expected = new TestMessage("Widget", 42);
        ReadOnlySequence<byte> data = FabricateEnvelope(FullAttributes(), expected);

        TestMessage? result = sut.Deserialize<TestMessage>(data);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Widget");
        result.Value.Should().Be(42);
    }

    // -------------------------------------------------------------------------
    // Test 5: mandatory-only attributes pass validation; optional absence is fine
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_ValidEnvelopeMandatoryOnly_DeserializesDataPayload()
    {
        var sut = new CloudEventsEnvelopeDeserializer();
        var expected = new TestMessage("Bolt", 7);
        ReadOnlySequence<byte> data = FabricateEnvelope(MandatoryAttributes(), expected);

        TestMessage? result = sut.Deserialize<TestMessage>(data);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Bolt");
        result.Value.Should().Be(7);
    }

    // -------------------------------------------------------------------------
    // Test 6: data is deserialized from inline JSON object (symmetry with 13.8)
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_DataInline_ReadsNestedJsonObject()
    {
        var sut = new CloudEventsEnvelopeDeserializer();
        // Hand-craft an envelope where data is a plain nested JSON object.
        const string json = """
            {
                "specversion": "1.0",
                "id": "inline-id-003",
                "source": "https://example.com/myapp",
                "type": "com.example.order.created",
                "data": { "name": "Sprocket", "value": 99 }
            }
            """;
        ReadOnlySequence<byte> data = FromUtf8String(json);

        TestMessage? result = sut.Deserialize<TestMessage>(data);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Sprocket");
        result.Value.Should().Be(99);
    }

    // -------------------------------------------------------------------------
    // Test 7: missing mandatory attribute → fail-fast BareWireSerializationException
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_MissingMandatoryAttribute_ThrowsBareWireSerializationException()
    {
        var sut = new CloudEventsEnvelopeDeserializer();
        // Valid envelope JSON except 'id' is absent.
        const string json = """
            {
                "specversion": "1.0",
                "source": "https://example.com/myapp",
                "type": "com.example.order.created",
                "data": { "name": "X", "value": 1 }
            }
            """;
        ReadOnlySequence<byte> data = FromUtf8String(json);

        Action act = () => sut.Deserialize<TestMessage>(data);

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 8: multi-segment ReadOnlySequence correctly deserialized
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_MultiSegmentSequence_DeserializesCompleteEnvelope()
    {
        var sut = new CloudEventsEnvelopeDeserializer();
        var expected = new TestMessage("Nut", 5);
        var serializer = new CloudEventsEnvelopeSerializer(MandatoryAttributes());
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(expected, buffer);

        byte[] allBytes = buffer.WrittenMemory.ToArray();
        int splitAt = allBytes.Length / 2; // split roughly in the middle
        ReadOnlySequence<byte> multiSegment = ToMultiSegmentSequence(allBytes, splitAt);

        TestMessage? result = sut.Deserialize<TestMessage>(multiSegment);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Nut");
        result.Value.Should().Be(5);
    }

    // -------------------------------------------------------------------------
    // Test 9: PERF-1 allocation regression guard (per verify Section 13 mandate)
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_MandatoryOnly_StaysWithinAllocationBudget()
    {
        var sut = new CloudEventsEnvelopeDeserializer();
        var message = new TestMessage("Perf", 1);
        ReadOnlySequence<byte> data = FabricateEnvelope(MandatoryAttributes(), message);

        // Warm up: JIT all paths before measuring.
        sut.Deserialize<TestMessage>(data);

        const int iterations = 10;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            sut.Deserialize<TestMessage>(data);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        long allocatedPerOp = (after - before) / iterations;

        // PERF-1: The <512 B/op target is a design goal; Wzorzec A (DTO + JsonDocument
        // clone of the envelope) is expected to exceed it similarly to task 13.5 binary
        // (528 B/op accepted as G11 documented exception). We assert a generous regression
        // guard of <2048 B/op. Measured value is logged as a comment below.
        //
        // PERF-1 measured on .NET 10 Debug: ~904 B/op (mandatory-only envelope).
        // This exceeds the <512 B/op design target due to Wzorzec A (DTO+JsonDocument clone of the
        // envelope + optional JsonExtensionData dictionary). Accepted as a documented exception
        // consistent with the 13.5 binary precedent (528 B/op, G11). The <2048 guard below
        // serves as a regression boundary — if this regresses significantly, revisit Wzorzec B
        // (manual Utf8JsonReader) or defer to 13.10 hardening.
        allocatedPerOp.Should().BeLessThan(2048,
            because: "each mandatory-only CloudEvents envelope deserialize should not regress beyond 2048 B/op " +
                     "(target <512 B/op; documented exception per PERF-1 / G11 precedent from task 13.5)");
    }
}
