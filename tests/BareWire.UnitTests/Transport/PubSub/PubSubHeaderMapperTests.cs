using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.Google.PubSub;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubHeaderMapperTests
{
    // ── MapOutbound — round-trip ──────────────────────────────────────────────

    [Fact]
    public void MapOutbound_WithHeaders_ReturnsMatchingAttributes()
    {
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["correlation-id"] = "abc-123",
        };

        Dictionary<string, string> result = PubSubHeaderMapper.MapOutbound(headers);

        result.Should().HaveCount(2);
        result["content-type"].Should().Be("application/json");
        result["correlation-id"].Should().Be("abc-123");
    }

    [Fact]
    public void MapOutbound_EmptyHeaders_ReturnsEmptyDictionary()
    {
        Dictionary<string, string> result = PubSubHeaderMapper.MapOutbound(
            new Dictionary<string, string>());

        result.Should().BeEmpty();
    }

    // ── MapInbound — round-trip ───────────────────────────────────────────────

    [Fact]
    public void MapInbound_WithAttributes_ReturnsMatchingHeaders()
    {
        var attrs = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["correlation-id"] = "xyz-789",
        };

        Dictionary<string, string> result = PubSubHeaderMapper.MapInbound(attrs);

        result.Should().HaveCount(2);
        result["content-type"].Should().Be("application/json");
        result["correlation-id"].Should().Be("xyz-789");
    }

    [Fact]
    public void MapInbound_NullAttributes_ReturnsEmptyDictionary()
    {
        Dictionary<string, string> result = PubSubHeaderMapper.MapInbound(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapInbound_EmptyAttributes_ReturnsEmptyDictionary()
    {
        Dictionary<string, string> result = PubSubHeaderMapper.MapInbound(
            new Dictionary<string, string>());

        result.Should().BeEmpty();
    }

    // ── MapOutbound — round-trip (MapOutbound then MapInbound) ────────────────

    [Fact]
    public void MapOutbound_ThenMapInbound_PreservesAllHeaders()
    {
        var originalHeaders = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["BW-OrderingKey"] = "partition-1",
            ["correlation-id"] = "round-trip-test",
        };

        Dictionary<string, string> attributes = PubSubHeaderMapper.MapOutbound(originalHeaders);
        Dictionary<string, string> recovered = PubSubHeaderMapper.MapInbound(attributes);

        recovered.Should().BeEquivalentTo(originalHeaders);
    }

    // ── MapOutbound — attribute count limit (SEC-4 / SEC-1) ──────────────────

    [Fact]
    public void MapOutbound_ExactlyOneHundredHeaders_Succeeds()
    {
        var headers = Enumerable.Range(0, 100)
            .ToDictionary(i => $"header-{i}", i => $"value-{i}");

        // Should not throw at exactly the limit.
        Func<Dictionary<string, string>> act = () => PubSubHeaderMapper.MapOutbound(headers);

        act.Should().NotThrow();
    }

    [Fact]
    public void MapOutbound_OneHundredAndOneHeaders_ThrowsBareWireTransportException()
    {
        var headers = Enumerable.Range(0, 101)
            .ToDictionary(i => $"header-{i}", i => $"value-{i}");

        Action act = () => PubSubHeaderMapper.MapOutbound(headers);

        BareWireTransportException ex = act.Should().ThrowExactly<BareWireTransportException>().Which;

        // SEC-4: message must contain the count — never header names or values.
        ex.Message.Should().Contain("101",
            "the exception message must report the actual header count");
        ex.Message.Should().Contain("100",
            "the exception message must report the Pub/Sub limit");

        // Ensure no header value text leaked into the message (SEC-4).
        ex.Message.Should().NotContain("value-",
            "exception must not contain header value text (SEC-4)");
    }

    // ── MapOutbound — key byte limit (SEC-1) ─────────────────────────────────

    [Fact]
    public void MapOutbound_OversizedKey_ThrowsBareWireTransportException_WithLengthNotKeyText()
    {
        // Key of 257 UTF-8 bytes (1 byte per ASCII char).
        string oversizedKey = new string('k', 257);
        var headers = new Dictionary<string, string> { [oversizedKey] = "value" };

        Action act = () => PubSubHeaderMapper.MapOutbound(headers);

        BareWireTransportException ex = act.Should().ThrowExactly<BareWireTransportException>().Which;

        // SEC-4: message must contain byte length — never the key text itself.
        ex.Message.Should().Contain("257",
            "exception must report the byte length of the oversized key");
        ex.Message.Should().NotContain(oversizedKey,
            "exception must NOT contain the key text (SEC-4)");
        ex.Message.Should().NotContain("value",
            "exception must NOT contain the value text (SEC-4)");
    }

    // ── MapOutbound — value byte limit (SEC-1) ────────────────────────────────

    [Fact]
    public void MapOutbound_OversizedValue_ThrowsBareWireTransportException_WithLengthNotValueText()
    {
        // Value of 1025 UTF-8 bytes.
        string oversizedValue = new string('v', 1025);
        var headers = new Dictionary<string, string> { ["my-header"] = oversizedValue };

        Action act = () => PubSubHeaderMapper.MapOutbound(headers);

        BareWireTransportException ex = act.Should().ThrowExactly<BareWireTransportException>().Which;

        // SEC-4: message must contain byte length — never the value text itself.
        ex.Message.Should().Contain("1025",
            "exception must report the byte length of the oversized value");
        ex.Message.Should().NotContain(oversizedValue,
            "exception must NOT contain the value text (SEC-4)");
        ex.Message.Should().NotContain("my-header",
            "exception must NOT contain the key text (SEC-4)");
    }

    // ── EstimateMessageBytes ──────────────────────────────────────────────────

    [Fact]
    public void EstimateMessageBytes_BodyOnly_ReturnsBodyLength()
    {
        long estimate = PubSubHeaderMapper.EstimateMessageBytes(
            bodyLength: 100,
            headers: new Dictionary<string, string>(),
            orderingKey: string.Empty);

        estimate.Should().Be(100);
    }

    [Fact]
    public void EstimateMessageBytes_WithHeadersAndOrderingKey_IncludesAllBytes()
    {
        // header: "k"(1) + "v"(1) = 2 bytes; ordering key: "ord"(3) = 3 bytes; body = 50 bytes.
        long estimate = PubSubHeaderMapper.EstimateMessageBytes(
            bodyLength: 50,
            headers: new Dictionary<string, string> { ["k"] = "v" },
            orderingKey: "ord");

        estimate.Should().Be(50 + 1 + 1 + 3);
    }
}
