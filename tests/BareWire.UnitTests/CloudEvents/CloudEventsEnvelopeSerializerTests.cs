using System.Buffers;
using System.Text.Json;

using AwesomeAssertions;

using BareWire.Abstractions.Exceptions;
using BareWire.CloudEvents;

namespace BareWire.UnitTests.CloudEvents;

public sealed class CloudEventsEnvelopeSerializerTests
{
    // -------------------------------------------------------------------------
    // Test message payload type
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

    private static CloudEventsEnvelopeSerializer CreateSerializer(ICloudEventAttributes? attributes = null)
        => new(attributes ?? MandatoryAttributes());

    private static JsonDocument SerializeToDocument(CloudEventsEnvelopeSerializer serializer, TestMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(message, buffer);
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    // -------------------------------------------------------------------------
    // Test 1: ContentType property
    // -------------------------------------------------------------------------

    [Fact]
    public void ContentType_Always_ReturnsApplicationCloudEventsJson()
    {
        CloudEventsEnvelopeSerializer sut = CreateSerializer();

        sut.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 2: mandatory CE attributes are present in the envelope
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_MandatoryAttributes_WrittenInEnvelope()
    {
        CloudEventsEnvelopeSerializer sut = CreateSerializer(MandatoryAttributes());
        var message = new TestMessage("Widget", 7);

        using JsonDocument doc = SerializeToDocument(sut, message);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("specversion", out JsonElement specversion).Should().BeTrue();
        specversion.GetString().Should().Be("1.0");

        root.TryGetProperty("id", out JsonElement id).Should().BeTrue();
        id.GetString().Should().Be("test-id-001");

        root.TryGetProperty("source", out JsonElement source).Should().BeTrue();
        source.GetString().Should().Be("https://example.com/myapp");

        root.TryGetProperty("type", out JsonElement type).Should().BeTrue();
        type.GetString().Should().Be("com.example.order.created");
    }

    // -------------------------------------------------------------------------
    // Test 3: data written inline as a JSON object (not string / base64)
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_Data_WrittenInlineAsJsonObject()
    {
        CloudEventsEnvelopeSerializer sut = CreateSerializer();
        var message = new TestMessage("Widget", 42);

        using JsonDocument doc = SerializeToDocument(sut, message);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("data", out JsonElement data).Should().BeTrue();

        // data must be a JSON object, NOT a string or base64 blob.
        data.ValueKind.Should().Be(JsonValueKind.Object);

        // The object must contain the message properties (camelCase from JsonSerializerDefaults.Web).
        data.TryGetProperty("name", out JsonElement name).Should().BeTrue();
        name.GetString().Should().Be("Widget");

        data.TryGetProperty("value", out JsonElement value).Should().BeTrue();
        value.GetInt32().Should().Be(42);
    }

    // -------------------------------------------------------------------------
    // Test 4: optional attributes and extensions written when present; absent when null
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_OptionalAttributesAndExtensions_WrittenWhenPresent()
    {
        // --- full attributes: all optional fields must appear ---
        CloudEventsEnvelopeSerializer sutFull = CreateSerializer(FullAttributes());
        var message = new TestMessage("Gadget", 1);

        using JsonDocument docFull = SerializeToDocument(sutFull, message);
        JsonElement rootFull = docFull.RootElement;

        rootFull.TryGetProperty("subject", out JsonElement subject).Should().BeTrue();
        subject.GetString().Should().Be("order/42");

        rootFull.TryGetProperty("time", out JsonElement time).Should().BeTrue();
        time.GetString().Should().NotBeNullOrEmpty();

        rootFull.TryGetProperty("datacontenttype", out JsonElement dct).Should().BeTrue();
        dct.GetString().Should().Be("application/json");

        rootFull.TryGetProperty("dataschema", out JsonElement ds).Should().BeTrue();
        ds.GetString().Should().Be("https://schemas.example.com/v1/order.json");

        rootFull.TryGetProperty("traceparent", out JsonElement tp).Should().BeTrue();
        tp.GetString().Should().Be("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        rootFull.TryGetProperty("partitionkey", out JsonElement pk).Should().BeTrue();
        pk.GetString().Should().Be("tenant-42");

        // --- mandatory-only attributes: optional fields must be ABSENT ---
        CloudEventsEnvelopeSerializer sutMandatory = CreateSerializer(MandatoryAttributes());

        using JsonDocument docMandatory = SerializeToDocument(sutMandatory, message);
        JsonElement rootMandatory = docMandatory.RootElement;

        rootMandatory.TryGetProperty("subject", out _).Should().BeFalse();
        rootMandatory.TryGetProperty("time", out _).Should().BeFalse();
        rootMandatory.TryGetProperty("datacontenttype", out _).Should().BeFalse();
        rootMandatory.TryGetProperty("dataschema", out _).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 5: well-formed CloudEvents JSON (full round-trip parse)
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_ValidEnvelope_ProducesWellFormedCloudEventsJson()
    {
        CloudEventsEnvelopeSerializer sut = CreateSerializer(FullAttributes());
        var message = new TestMessage("Sprocket", 99);

        using JsonDocument doc = SerializeToDocument(sut, message);
        JsonElement root = doc.RootElement;

        // Root is an object (not array/scalar).
        root.ValueKind.Should().Be(JsonValueKind.Object);

        // All four mandatory CE attributes are present with correct values.
        root.GetProperty("specversion").GetString().Should().Be("1.0");
        root.GetProperty("id").GetString().Should().Be("full-id-002");
        root.GetProperty("source").GetString().Should().Be("https://example.com/myapp");
        root.GetProperty("type").GetString().Should().Be("com.example.order.created");

        // data is a nested JSON object.
        root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Object);

        // time is RFC3339-formatted (parseable as DateTimeOffset).
        string? timeStr = root.GetProperty("time").GetString();
        timeStr.Should().NotBeNullOrEmpty();
        DateTimeOffset.TryParse(timeStr, out _).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Test 6a: null-guard tests (message, output, constructor attributes)
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_NullMessageOrOutput_ThrowsArgumentNullException()
    {
        CloudEventsEnvelopeSerializer sut = CreateSerializer();
        var buffer = new ArrayBufferWriter<byte>();
        var message = new TestMessage("X", 0);

        // null message
        Action actNullMessage = () => sut.Serialize<TestMessage>(null!, buffer);
        actNullMessage.Should().ThrowExactly<ArgumentNullException>()
            .Which.ParamName.Should().Be("message");

        // null output
        Action actNullOutput = () => sut.Serialize(message, (IBufferWriter<byte>)null!);
        actNullOutput.Should().ThrowExactly<ArgumentNullException>()
            .Which.ParamName.Should().Be("output");
    }

    [Fact]
    public void Constructor_NullAttributes_ThrowsArgumentNullException()
    {
        // Wrap in a local function to avoid CA1806 (object creation result discarded).
        static void Construct() => _ = new CloudEventsEnvelopeSerializer(null!);
        Action act = Construct;
        act.Should().ThrowExactly<ArgumentNullException>()
            .Which.ParamName.Should().Be("attributes");
    }

    // -------------------------------------------------------------------------
    // Test 7: multi-segment IBufferWriter still produces complete envelope
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_IntoMultiSegmentBufferWriter_ProducesCompleteEnvelope()
    {
        CloudEventsEnvelopeSerializer sut = CreateSerializer(MandatoryAttributes());
        var message = new TestMessage("Bolt", 5);

        // ArrayBufferWriter is single-segment internally but confirms the contract
        // that Flush() drives all bytes into the writer regardless of segment layout.
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 16); // tiny initial capacity
        sut.Serialize(message, buffer);

        // The written memory must be non-empty and parse as a valid JSON document.
        buffer.WrittenCount.Should().BeGreaterThan(0);

        using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);
        JsonElement root = doc.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.TryGetProperty("specversion", out _).Should().BeTrue();
        root.TryGetProperty("data", out JsonElement data).Should().BeTrue();
        data.ValueKind.Should().Be(JsonValueKind.Object);
    }

    // -------------------------------------------------------------------------
    // Test 8: fail-fast validation — invalid mandatory attribute → BareWireSerializationException
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_EmptyMandatoryId_ThrowsBareWireSerializationException()
    {
        // CloudEventContext ctor guards against null/empty id, so we use a minimal
        // test double to exercise the validator path with a blank Id value.
        var fakeAttributes = new FakeAttributes(id: string.Empty);
        CloudEventsEnvelopeSerializer sut = new(fakeAttributes);
        var buffer = new ArrayBufferWriter<byte>();
        var message = new TestMessage("X", 0);

        Action act = () => sut.Serialize(message, buffer);

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test double — allows injecting invalid attribute state that CloudEventContext
    // ctor would otherwise reject (e.g. empty Id for validator path testing).
    // -------------------------------------------------------------------------

    private sealed class FakeAttributes : ICloudEventAttributes
    {
        public FakeAttributes(
            string id = "fake-id",
            string specVersion = "1.0",
            string type = "com.example.fake")
        {
            Id = id;
            SpecVersion = specVersion;
            Type = type;
        }

        public string Id { get; }
        public Uri Source { get; } = new Uri("https://example.com/fake");
        public string SpecVersion { get; }
        public string Type { get; }
        public string? Subject => null;
        public DateTimeOffset? Time => null;
        public string? DataContentType => null;
        public Uri? DataSchema => null;
        public IReadOnlyDictionary<string, string> Extensions { get; } =
            new Dictionary<string, string>(0);
    }
}
