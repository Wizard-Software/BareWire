using AwesomeAssertions;

using BareWire.CloudEvents;

namespace BareWire.UnitTests.CloudEvents;

public sealed class CloudEventBinaryHeaderMapperTests
{
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
        dataSchema: new Uri("https://schemas.example.com/v1/order.json"));

    // -------------------------------------------------------------------------
    // Test 1: mandatory attributes round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void ToHeaders_ThenTryFromHeaders_MandatoryAttributes_RoundtripsEquivalent()
    {
        CloudEventContext original = MandatoryAttributes();

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(original);
        bool result = CloudEventBinaryHeaderMapper.TryFromHeaders(
            (IReadOnlyDictionary<string, string>)headers, out ICloudEventAttributes? parsed);

        result.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Id.Should().Be(original.Id);
        parsed.Source.OriginalString.Should().Be(original.Source.OriginalString);
        parsed.SpecVersion.Should().Be(original.SpecVersion);
        parsed.Type.Should().Be(original.Type);
    }

    // -------------------------------------------------------------------------
    // Test 2: optional attributes round-trip (incl. Time with RFC3339 roundtrip)
    // -------------------------------------------------------------------------

    [Fact]
    public void ToHeaders_ThenTryFromHeaders_OptionalAttributes_RoundtripsEquivalent()
    {
        CloudEventContext original = FullAttributes();

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(original);
        bool result = CloudEventBinaryHeaderMapper.TryFromHeaders(
            (IReadOnlyDictionary<string, string>)headers, out ICloudEventAttributes? parsed);

        result.Should().BeTrue();
        parsed.Should().NotBeNull();

        // Mandatory attributes survive round-trip.
        parsed!.Id.Should().Be(original.Id);
        parsed.Source.OriginalString.Should().Be(original.Source.OriginalString);
        parsed.SpecVersion.Should().Be(original.SpecVersion);
        parsed.Type.Should().Be(original.Type);

        // Optional attributes survive round-trip.
        parsed.Subject.Should().Be(original.Subject);
        parsed.DataContentType.Should().Be(original.DataContentType);
        parsed.DataSchema.Should().NotBeNull();
        parsed.DataSchema!.OriginalString.Should().Be(original.DataSchema!.OriginalString);

        // RFC3339 roundtrip: Time must preserve the UTC offset (not convert to UTC).
        parsed.Time.Should().NotBeNull();
        parsed.Time!.Value.Should().Be(original.Time!.Value);
        parsed.Time!.Value.Offset.Should().Be(original.Time!.Value.Offset);
    }

    // -------------------------------------------------------------------------
    // Test 3: extension attributes round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void ToHeaders_ThenTryFromHeaders_Extensions_RoundtripsKeysAndValues()
    {
        var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ce-traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["ce-partitionkey"] = "tenant-42",
        };

        var original = new CloudEventContext(
            id: "ext-id-003",
            source: new Uri("https://example.com/src"),
            type: "com.example.test",
            extensions: extensions);

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(original);
        bool result = CloudEventBinaryHeaderMapper.TryFromHeaders(
            (IReadOnlyDictionary<string, string>)headers, out ICloudEventAttributes? parsed);

        result.Should().BeTrue();
        parsed.Should().NotBeNull();

        // Extension keys are present in the parsed Extensions dictionary.
        parsed!.Extensions.Should().ContainKey("ce-traceparent");
        parsed.Extensions["ce-traceparent"].Should().Be(extensions["ce-traceparent"]);
        parsed.Extensions.Should().ContainKey("ce-partitionkey");
        parsed.Extensions["ce-partitionkey"].Should().Be(extensions["ce-partitionkey"]);

        // Extension keys must NOT appear as standard attributes.
        parsed.Subject.Should().BeNull();
        parsed.DataContentType.Should().BeNull();
        parsed.DataSchema.Should().BeNull();
        parsed.Time.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Test 4: all emitted keys carry the ce- prefix
    // -------------------------------------------------------------------------

    [Fact]
    public void ToHeaders_AllEmittedKeys_StartWithCePrefix()
    {
        CloudEventContext attributes = FullAttributes();

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(attributes);

        headers.Keys.Should().AllSatisfy(
            key => key.ToLowerInvariant().Should().StartWith("ce-"));
    }

    // -------------------------------------------------------------------------
    // Test 5: no data / envelope keys emitted (ADR-001 raw-first)
    // -------------------------------------------------------------------------

    [Fact]
    public void ToHeaders_DoesNotEmitDataOrEnvelopeKeys()
    {
        CloudEventContext attributes = FullAttributes();

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(attributes);

        // ADR-001: payload is raw — the mapper must not produce structured-mode envelope keys.
        headers.Keys.Should().NotContain("data");
        headers.Keys.Should().NotContain("data_base64");
        headers.Keys.Should().NotContain("dataBase64");
        headers.Keys.Should().NotContain("specversion");  // no un-prefixed attribute
        headers.Keys.Should().NotContain("id");
        headers.Keys.Should().NotContain("source");
        headers.Keys.Should().NotContain("type");
    }

    // -------------------------------------------------------------------------
    // Test 6a: TryFromHeaders returns false/null when a mandatory header is missing
    // Test 6b: TryFromHeaders returns false/null when a mandatory header is present but unparseable
    // Test 6c: structural assertion — plain ce-* string dictionary (not AMQP 1.0 artifacts)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryFromHeaders_WhenMandatoryHeaderMissing_ReturnsFalseAndNull()
    {
        // Omit ce-id — one of the four mandatory headers.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CloudEventBinaryHeaderMapper.HeaderSource] = "https://example.com/src",
            [CloudEventBinaryHeaderMapper.HeaderSpecVersion] = "1.0",
            [CloudEventBinaryHeaderMapper.HeaderType] = "com.example.test",
        };

        bool result = CloudEventBinaryHeaderMapper.TryFromHeaders(headers, out ICloudEventAttributes? attributes);

        result.Should().BeFalse();
        attributes.Should().BeNull();
    }

    [Fact]
    public void TryFromHeaders_WhenMandatorySourceUnparseable_ReturnsFalseAndNull()
    {
        // ce-source present but contains a value that is an invalid URI (e.g. empty string,
        // which passes TryGetValue but fails the IsNullOrEmpty guard).
        var headersWithEmptySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CloudEventBinaryHeaderMapper.HeaderId] = "some-id",
            [CloudEventBinaryHeaderMapper.HeaderSource] = string.Empty,
            [CloudEventBinaryHeaderMapper.HeaderSpecVersion] = "1.0",
            [CloudEventBinaryHeaderMapper.HeaderType] = "com.example.test",
        };

        bool result = CloudEventBinaryHeaderMapper.TryFromHeaders(
            headersWithEmptySource, out ICloudEventAttributes? attributes);

        result.Should().BeFalse();
        attributes.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Test 6d: TryFromHeaders returns false/null when ce-source is a non-empty
    // string that passes IsNullOrEmpty but fails Uri.TryCreate (exercises line
    // 120-123 in CloudEventBinaryHeaderMapper — the Uri.TryCreate failure branch
    // that the empty-string test above never reaches).
    //
    // "http://[" is a syntactically invalid URI (unclosed IPv6 bracket) that is
    // non-empty yet makes Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out _)
    // return false on .NET 10 (verified empirically).  UriKind.RelativeOrAbsolute
    // accepts almost any string as a relative URI, but the absolute-URI path still
    // validates the authority component — an unclosed bracket is rejected.
    // -------------------------------------------------------------------------

    [Fact]
    public void TryFromHeaders_WhenMandatorySourceFailsUriTryCreate_ReturnsFalseAndNull()
    {
        // "http://[" is non-empty (passes IsNullOrEmpty) but fails
        // Uri.TryCreate(sourceRaw, UriKind.RelativeOrAbsolute, out _) because
        // the IPv6 literal bracket is never closed — the exact branch under test.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CloudEventBinaryHeaderMapper.HeaderId] = "some-id",
            [CloudEventBinaryHeaderMapper.HeaderSource] = "http://[",
            [CloudEventBinaryHeaderMapper.HeaderSpecVersion] = "1.0",
            [CloudEventBinaryHeaderMapper.HeaderType] = "com.example.test",
        };

        bool result = CloudEventBinaryHeaderMapper.TryFromHeaders(headers, out ICloudEventAttributes? attributes);

        result.Should().BeFalse();
        attributes.Should().BeNull();
    }

    [Fact]
    public void Mapper_IsAmqp091HeaderMapping_NotCertifiedAmqp10Binding()
    {
        // Structural assertion (ADR-007 §R1): ToHeaders produces a plain string→string dictionary
        // with ce-* header names. There must be no AMQP 1.0 property artifacts (typed value objects,
        // application-properties section wrappers, etc.) — all values are plain strings, all keys
        // are ce-* names.
        CloudEventContext attributes = MandatoryAttributes();

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(attributes);

        // All keys are strings beginning with "ce-" (AMQP 0-9-1 header map model).
        headers.Keys.Should().AllSatisfy(
            key => key.ToLowerInvariant().Should().StartWith("ce-"));

        // All values are plain strings (not typed AMQP 1.0 property objects).
        headers.Values.Should().AllSatisfy(
            value => value.Should().BeOfType<string>());

        // The dictionary type itself is IDictionary<string,string> — no transport-specific wrapper.
        headers.Should().BeAssignableTo<IDictionary<string, string>>();
    }
}
