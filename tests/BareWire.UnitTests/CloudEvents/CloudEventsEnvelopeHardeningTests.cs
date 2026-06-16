using System.Buffers;
using System.Text;

using AwesomeAssertions;

using BareWire.Abstractions.Exceptions;
using BareWire.CloudEvents;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// SEC-1 hardening tests for <see cref="CloudEventsEnvelopeDeserializer"/> pre-scan limits.
/// Each negative test exercises a distinct hardening rule; the positive test guards regression
/// of the 13.9 happy path.
/// </summary>
public sealed class CloudEventsEnvelopeHardeningTests
{
    // -------------------------------------------------------------------------
    // Payload type
    // -------------------------------------------------------------------------

    private sealed record TestPayload(string Name, int Value);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ReadOnlySequence<byte> FromUtf8String(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return new ReadOnlySequence<byte>(bytes);
    }

    /// <summary>
    /// Builds a minimal valid mandatory-only CE 1.0 structured-mode envelope JSON string.
    /// </summary>
    private static string MandatoryEnvelope(string dataJson = """{"name":"x","value":1}""")
        => $$"""
             {
                 "specversion": "1.0",
                 "id": "test-id",
                 "source": "https://example.com",
                 "type": "com.example.test",
                 "data": {{dataJson}}
             }
             """;

    // -------------------------------------------------------------------------
    // Test 1 — Rule 2: attribute count exceeds MaxAttributeCount
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_AttributeCountExceedsLimit_Throws()
    {
        // Limits: MaxAttributeCount = 5; a valid mandatory envelope already has 5
        // (specversion, id, source, type, data). Adding even one extension exceeds it.
        var limits = new CloudEventsEnvelopeLimits(
            maxEnvelopeSizeBytes: 65536,
            maxAttributeCount: 5,
            maxAttributeValueLength: 4096,
            maxExtensionNameLength: 64,
            maxDataDepth: 32);

        // Build envelope with 4 mandatory attributes + data + 1 extension (total 6 > 5)
        const string json = """
            {
                "specversion": "1.0",
                "id": "test-id",
                "source": "https://example.com",
                "type": "com.example.test",
                "data": {"name":"x","value":1},
                "myext": "value"
            }
            """;

        var sut = new CloudEventsEnvelopeDeserializer(limits);
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(json));

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 2 — Rule 1: envelope size exceeds MaxEnvelopeSizeBytes
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_EnvelopeSizeExceedsLimit_Throws()
    {
        // Use a tiny MaxEnvelopeSizeBytes so the envelope JSON itself exceeds it
        var limits = new CloudEventsEnvelopeLimits(
            maxEnvelopeSizeBytes: 10,
            maxAttributeCount: 64,
            maxAttributeValueLength: 4096,
            maxExtensionNameLength: 64,
            maxDataDepth: 32);

        var sut = new CloudEventsEnvelopeDeserializer(limits);
        // The envelope JSON is well above 10 bytes
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(MandatoryEnvelope()));

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 3 — Rule 4: extension name contains uppercase (invalid per CE 1.0)
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_InvalidExtensionNameUppercase_Throws()
    {
        var limits = CloudEventsEnvelopeLimits.Default;

        // "myExtension" has uppercase 'E' — must be rejected
        const string json = """
            {
                "specversion": "1.0",
                "id": "test-id",
                "source": "https://example.com",
                "type": "com.example.test",
                "data": {"name":"x","value":1},
                "myExtension": "value"
            }
            """;

        var sut = new CloudEventsEnvelopeDeserializer(limits);
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(json));

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 4 — Rule 4: extension name contains special chars (invalid per CE 1.0)
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_InvalidExtensionNameSpecialChars_Throws()
    {
        var limits = CloudEventsEnvelopeLimits.Default;

        // "my-ext" has a hyphen — must be rejected
        const string json = """
            {
                "specversion": "1.0",
                "id": "test-id",
                "source": "https://example.com",
                "type": "com.example.test",
                "data": {"name":"x","value":1},
                "my-ext": "value"
            }
            """;

        var sut = new CloudEventsEnvelopeDeserializer(limits);
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(json));

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 5 — Rule 5: duplicate standard context attribute
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_DuplicateContextAttribute_Throws()
    {
        var limits = CloudEventsEnvelopeLimits.Default;

        // Two "id" properties — STJ last-wins silently; hardening must reject
        const string json = """
            {
                "specversion": "1.0",
                "id": "first-id",
                "id": "second-id",
                "source": "https://example.com",
                "type": "com.example.test",
                "data": {"name":"x","value":1}
            }
            """;

        var sut = new CloudEventsEnvelopeDeserializer(limits);
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(json));

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 6 — positive: valid envelope with extensions stays within Default limits
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_WithinLimits_Succeeds()
    {
        var sut = new CloudEventsEnvelopeDeserializer(CloudEventsEnvelopeLimits.Default);

        // Valid envelope: 4 mandatory + 2 valid lowercase-alphanumeric extensions + data
        const string json = """
            {
                "specversion": "1.0",
                "id": "test-id",
                "source": "https://example.com",
                "type": "com.example.test",
                "traceparent": "00-abc123def456-01",
                "partitionkey": "tenant42",
                "data": {"name":"Hello","value":99}
            }
            """;

        TestPayload? result = sut.Deserialize<TestPayload>(FromUtf8String(json));

        result.Should().NotBeNull();
        result!.Name.Should().Be("Hello");
        result.Value.Should().Be(99);
    }

    // -------------------------------------------------------------------------
    // Test 7 — Rule 3: scalar attribute value exceeds MaxAttributeValueLength
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_AttributeValueExceedsLimit_Throws()
    {
        // MaxAttributeValueLength = 16 — the "type" value is 20 chars long
        var limits = new CloudEventsEnvelopeLimits(
            maxEnvelopeSizeBytes: 65536,
            maxAttributeCount: 64,
            maxAttributeValueLength: 16,
            maxExtensionNameLength: 64,
            maxDataDepth: 32);

        // "type" value "com.example.test.long" is 21 chars > 16
        const string json = """
            {
                "specversion": "1.0",
                "id": "x",
                "source": "https://example.com",
                "type": "com.example.test.long",
                "data": {"name":"x","value":1}
            }
            """;

        var sut = new CloudEventsEnvelopeDeserializer(limits);
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(json));

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 8 — SEC-2: non-scalar extension value exceeds MaxAttributeValueLength
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_NonScalarExtensionValue_Throws()
    {
        // MaxAttributeValueLength = 5 — the extension object subtree is larger than 5 bytes
        var limits = new CloudEventsEnvelopeLimits(
            maxEnvelopeSizeBytes: 65536,
            maxAttributeCount: 64,
            maxAttributeValueLength: 5,
            maxExtensionNameLength: 64,
            maxDataDepth: 32);

        // "myext" has a nested object value — CE 1.0 allows only scalars for extensions
        const string json = """
            {
                "specversion": "1.0",
                "id": "x",
                "source": "https://example.com",
                "type": "com.example.test",
                "myext": {"nested": "value"},
                "data": {"name":"x","value":1}
            }
            """;

        var sut = new CloudEventsEnvelopeDeserializer(limits);
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(json));

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Test 9 — SEC-1 MaxDepth: deeply nested data throws (via bounded MaxDepth)
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_DeeplyNestedData_Throws()
    {
        // The static Bounded JsonSerializerOptions uses MaxDepth = 32
        // (CloudEventsEnvelopeLimits.Default.MaxDataDepth). Build a data payload nested
        // deeper than 32 levels. The envelope adds 1 level at root, so a 32-level-deep
        // 'data' value results in total JSON depth of 33, which exceeds the STJ MaxDepth.
        // Note: STJ MaxDepth is the absolute nesting depth from the document root.
        const int targetDepth = 33; // 1 (envelope root) + 32 (data nesting) = 33 total
        string deeplyNested = BuildNestedJson(targetDepth);

        string json = $$"""{"specversion":"1.0","id":"x","source":"https://example.com","type":"com.example.test","data":{{deeplyNested}}}""";

        // Use default limits (MaxDataDepth=32 → static Bounded options).
        var sut = new CloudEventsEnvelopeDeserializer(CloudEventsEnvelopeLimits.Default);
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(json));

        // SEC-1: exceeds MaxDataDepth → JsonException wrapped as BareWireSerializationException
        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // Builds a JSON object nested `depth` levels deep: {"a":{"a":{...{"a":"leaf"}...}}}
    private static string BuildNestedJson(int depth)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < depth; i++)
            sb.Append("""{"a":""");
        sb.Append("\"leaf\"");
        for (int i = 0; i < depth; i++)
            sb.Append('}');
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Test 10 — SEC-3: extension name exceeds MaxExtensionNameLength
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_ExtensionNameTooLong_Throws()
    {
        // MaxExtensionNameLength = 4 — extension name "toolong" is 7 chars
        var limits = new CloudEventsEnvelopeLimits(
            maxEnvelopeSizeBytes: 65536,
            maxAttributeCount: 64,
            maxAttributeValueLength: 4096,
            maxExtensionNameLength: 4,
            maxDataDepth: 32);

        const string json = """
            {
                "specversion": "1.0",
                "id": "x",
                "source": "https://example.com",
                "type": "com.example.test",
                "toolong": "value",
                "data": {"name":"x","value":1}
            }
            """;

        var sut = new CloudEventsEnvelopeDeserializer(limits);
        Action act = () => sut.Deserialize<TestPayload>(FromUtf8String(json));

        act.Should().ThrowExactly<BareWireSerializationException>()
            .Which.ContentType.Should().Be("application/cloudevents+json");
    }

    // -------------------------------------------------------------------------
    // Positive regression guard: Default limits don't break 13.9 happy path
    // -------------------------------------------------------------------------

    [Fact]
    public void Deserialize_ValidEnvelopeDefaultLimits_Succeeds()
    {
        var sut = new CloudEventsEnvelopeDeserializer();

        const string json = """
            {
                "specversion": "1.0",
                "id": "regression-id",
                "source": "https://example.com",
                "type": "com.example.regression",
                "subject": "order/1",
                "data": {"name":"Bolt","value":7}
            }
            """;

        TestPayload? result = sut.Deserialize<TestPayload>(FromUtf8String(json));

        result.Should().NotBeNull();
        result!.Name.Should().Be("Bolt");
        result.Value.Should().Be(7);
    }
}
