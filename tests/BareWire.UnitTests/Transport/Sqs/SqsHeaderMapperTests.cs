using Amazon.SQS.Model;
using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.AWS.SQS;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

public sealed class SqsHeaderMapperTests
{
    // ── MapOutbound ──────────────────────────────────────────────────────────

    [Fact]
    public void MapOutbound_EmptyHeaders_ReturnsEmptyDictionary()
    {
        var result = SqsHeaderMapper.MapOutbound(
            new Dictionary<string, string>(StringComparer.Ordinal));

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapOutbound_WithHeaders_MapsAllToStringAttribute()
    {
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["correlation-id"] = "abc-123",
        };

        Dictionary<string, MessageAttributeValue> result = SqsHeaderMapper.MapOutbound(headers);

        result.Should().HaveCount(2);
        result["content-type"].DataType.Should().Be("String");
        result["content-type"].StringValue.Should().Be("application/json");
        result["correlation-id"].StringValue.Should().Be("abc-123");
    }

    [Fact]
    public void MapOutbound_ExactlyTenHeaders_DoesNotThrow()
    {
        var headers = new Dictionary<string, string>();
        for (int i = 0; i < 10; i++)
        {
            headers[$"header-{i}"] = $"value-{i}";
        }

        Action act = () => SqsHeaderMapper.MapOutbound(headers);

        act.Should().NotThrow();
    }

    [Fact]
    public void MapOutbound_ElevenHeaders_ThrowsBareWireTransportException()
    {
        var headers = new Dictionary<string, string>();
        for (int i = 0; i < 11; i++)
        {
            headers[$"header-{i}"] = $"value-{i}";
        }

        Action act = () => SqsHeaderMapper.MapOutbound(headers);

        act.Should().Throw<BareWireTransportException>();
    }

    [Fact]
    public void MapOutbound_ElevenHeaders_ExceptionMessageContainsOnlyCount_NotHeaderValues()
    {
        // SEC-4: exception message must contain the count (11), never header values.
        const string sentinelValue = "super-secret-header-value";
        var headers = new Dictionary<string, string>();
        headers["secret-header"] = sentinelValue;
        for (int i = 0; i < 10; i++)
        {
            headers[$"header-{i}"] = $"value-{i}";
        }

        BareWireTransportException ex =
            Record.Exception(() => SqsHeaderMapper.MapOutbound(headers)) as BareWireTransportException
            ?? throw new InvalidOperationException("Expected BareWireTransportException.");

        ex.Message.Should().Contain("11",
            "exception message must contain the header count");
        ex.Message.Should().NotContain(sentinelValue,
            "exception message must never contain header values (SEC-4)");
        ex.Message.Should().NotContain("secret-header",
            "exception message must never contain header names (SEC-4)");
    }

    // ── MapInbound ──────────────────────────────────────────────────────────

    [Fact]
    public void MapInbound_NullAttributes_ReturnsEmptyDictionary()
    {
        Dictionary<string, string> result = SqsHeaderMapper.MapInbound(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapInbound_EmptyAttributes_ReturnsEmptyDictionary()
    {
        Dictionary<string, string> result = SqsHeaderMapper.MapInbound(
            new Dictionary<string, MessageAttributeValue>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapInbound_WithAttributes_ReturnsStringValues()
    {
        var attrs = new Dictionary<string, MessageAttributeValue>
        {
            ["content-type"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = "application/json",
            },
            ["trace-id"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = "xyz-789",
            },
        };

        Dictionary<string, string> result = SqsHeaderMapper.MapInbound(attrs);

        result.Should().HaveCount(2);
        result["content-type"].Should().Be("application/json");
        result["trace-id"].Should().Be("xyz-789");
    }

    // ── Round-trip ──────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_HeadersPreservedAfterMapOutboundAndMapInbound()
    {
        var original = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["correlation-id"] = "round-trip-42",
            ["x-custom"] = "hello",
        };

        Dictionary<string, MessageAttributeValue> outbound = SqsHeaderMapper.MapOutbound(original);

        Dictionary<string, string> inbound = SqsHeaderMapper.MapInbound(outbound);

        foreach (KeyValuePair<string, string> pair in original)
        {
            inbound.Should().ContainKey(pair.Key);
            inbound[pair.Key].Should().Be(pair.Value);
        }
    }

    // ── EncodeBodyAsString / DecodeBodyBytes ────────────────────────────────

    [Fact]
    public void EncodeBodyAsString_JsonContentType_UsesUtf8()
    {
        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes("{\"foo\":\"bar\"}");
        string result = SqsHeaderMapper.EncodeBodyAsString(jsonBytes, "application/json");

        result.Should().Be("{\"foo\":\"bar\"}");
    }

    [Fact]
    public void EncodeBodyAsString_BinaryContentType_UsesBase64()
    {
        byte[] binaryBytes = [0x01, 0x02, 0x03, 0xFF];
        string result = SqsHeaderMapper.EncodeBodyAsString(binaryBytes, "application/x-msgpack");

        result.Should().Be(Convert.ToBase64String(binaryBytes));
    }

    [Fact]
    public void DecodeBodyBytes_JsonContentType_RoundTripsCorrectly()
    {
        byte[] original = System.Text.Encoding.UTF8.GetBytes("{\"key\":\"value\"}");
        string encoded = SqsHeaderMapper.EncodeBodyAsString(original, "application/json");
        ReadOnlyMemory<byte> decoded = SqsHeaderMapper.DecodeBodyBytes(encoded, "application/json");

        decoded.ToArray().Should().Equal(original);
    }

    [Fact]
    public void DecodeBodyBytes_BinaryContentType_RoundTripsCorrectly()
    {
        byte[] original = [0xDE, 0xAD, 0xBE, 0xEF];
        string encoded = SqsHeaderMapper.EncodeBodyAsString(original, "application/x-msgpack");
        ReadOnlyMemory<byte> decoded = SqsHeaderMapper.DecodeBodyBytes(encoded, "application/x-msgpack");

        decoded.ToArray().Should().Equal(original);
    }
}
