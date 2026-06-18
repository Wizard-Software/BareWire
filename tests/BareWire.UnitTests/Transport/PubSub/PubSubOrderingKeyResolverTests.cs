using AwesomeAssertions;
using BareWire.Transport.Google.PubSub.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubOrderingKeyResolverTests
{
    // ── Priority 1: BW-OrderingKey header ────────────────────────────────────

    [Fact]
    public void Resolve_WithOrderingKeyHeader_ReturnsThatValue()
    {
        var headers = new Dictionary<string, string>
        {
            [PubSubOrderingKeyResolver.OrderingKeyHeaderName] = "partition-99",
        };

        string result = PubSubOrderingKeyResolver.Resolve(headers);

        result.Should().Be("partition-99");
    }

    [Fact]
    public void Resolve_OrderingKeyHeaderOverridesCorrelationId()
    {
        var headers = new Dictionary<string, string>
        {
            [PubSubOrderingKeyResolver.OrderingKeyHeaderName] = "explicit-key",
            [PubSubOrderingKeyResolver.CorrelationIdHeader] = "fallback-corr",
        };

        string result = PubSubOrderingKeyResolver.Resolve(headers);

        result.Should().Be("explicit-key",
            "BW-OrderingKey (priority 1) must win over correlation-id (priority 2)");
    }

    // ── Priority 2: correlation-id fallback ──────────────────────────────────

    [Fact]
    public void Resolve_WithCorrelationIdHeaderOnly_ReturnsThatValue()
    {
        var headers = new Dictionary<string, string>
        {
            [PubSubOrderingKeyResolver.CorrelationIdHeader] = "corr-abc-123",
        };

        string result = PubSubOrderingKeyResolver.Resolve(headers);

        result.Should().Be("corr-abc-123",
            "correlation-id must be used as fallback ordering key (priority 2)");
    }

    // ── Empty result: neither header present ─────────────────────────────────

    [Fact]
    public void Resolve_WithNeitherHeader_ReturnsEmpty()
    {
        var headers = new Dictionary<string, string>
        {
            ["some-other-header"] = "irrelevant",
        };

        string result = PubSubOrderingKeyResolver.Resolve(headers);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_WithEmptyHeaders_ReturnsEmpty()
    {
        var headers = new Dictionary<string, string>();

        string result = PubSubOrderingKeyResolver.Resolve(headers);

        result.Should().BeEmpty();
    }

    // ── Empty header values treated as absent ────────────────────────────────

    [Fact]
    public void Resolve_OrderingKeyHeaderEmptyValue_SkipsToCorrelationId()
    {
        var headers = new Dictionary<string, string>
        {
            [PubSubOrderingKeyResolver.OrderingKeyHeaderName] = "",
            [PubSubOrderingKeyResolver.CorrelationIdHeader] = "corr-fallback",
        };

        string result = PubSubOrderingKeyResolver.Resolve(headers);

        result.Should().Be("corr-fallback",
            "empty BW-OrderingKey must be treated as absent and fall through to correlation-id");
    }

    [Fact]
    public void Resolve_BothHeadersEmptyValues_ReturnsEmpty()
    {
        var headers = new Dictionary<string, string>
        {
            [PubSubOrderingKeyResolver.OrderingKeyHeaderName] = "",
            [PubSubOrderingKeyResolver.CorrelationIdHeader] = "",
        };

        string result = PubSubOrderingKeyResolver.Resolve(headers);

        result.Should().BeEmpty(
            "empty values for both headers must yield string.Empty (treated as absent)");
    }

    // ── Null guard ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NullHeaders_ThrowsArgumentNullException()
    {
        Action act = () => PubSubOrderingKeyResolver.Resolve(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("headers");
    }
}
