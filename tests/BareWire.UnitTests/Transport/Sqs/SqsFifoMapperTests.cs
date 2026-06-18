using AwesomeAssertions;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

public sealed class SqsFifoMapperTests
{
    // ── ResolveMessageGroupId ─────────────────────────────────────────────────

    [Fact]
    public void ResolveMessageGroupId_BwMessageGroupIdPresent_ReturnsBwMessageGroupId()
    {
        var headers = new Dictionary<string, string>
        {
            [SqsHeaderMapper.MessageGroupIdHeader] = "group-from-bw-header",
            [SqsHeaderMapper.CorrelationIdHeader] = "correlation-should-not-be-used",
        };

        string? result = SqsFifoMapper.ResolveMessageGroupId(headers);

        result.Should().Be("group-from-bw-header");
    }

    [Fact]
    public void ResolveMessageGroupId_OnlyCorrelationIdPresent_ReturnsCorrelationId()
    {
        var headers = new Dictionary<string, string>
        {
            [SqsHeaderMapper.CorrelationIdHeader] = "corr-id-fallback",
        };

        string? result = SqsFifoMapper.ResolveMessageGroupId(headers);

        result.Should().Be("corr-id-fallback");
    }

    [Fact]
    public void ResolveMessageGroupId_NeitherHeaderPresent_ReturnsNull()
    {
        var headers = new Dictionary<string, string>
        {
            ["some-other-header"] = "irrelevant",
        };

        string? result = SqsFifoMapper.ResolveMessageGroupId(headers);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveMessageGroupId_EmptyHeaders_ReturnsNull()
    {
        string? result = SqsFifoMapper.ResolveMessageGroupId(new Dictionary<string, string>());

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveMessageGroupId_BwMessageGroupIdEmptyString_FallsBackToCorrelationId()
    {
        // Empty string treated as absent — must fall through to correlation-id.
        var headers = new Dictionary<string, string>
        {
            [SqsHeaderMapper.MessageGroupIdHeader] = string.Empty,
            [SqsHeaderMapper.CorrelationIdHeader] = "corr-id",
        };

        string? result = SqsFifoMapper.ResolveMessageGroupId(headers);

        result.Should().Be("corr-id");
    }

    [Fact]
    public void ResolveMessageGroupId_BothHeadersEmpty_ReturnsNull()
    {
        var headers = new Dictionary<string, string>
        {
            [SqsHeaderMapper.MessageGroupIdHeader] = string.Empty,
            [SqsHeaderMapper.CorrelationIdHeader] = string.Empty,
        };

        string? result = SqsFifoMapper.ResolveMessageGroupId(headers);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveMessageGroupId_NullHeaders_ThrowsArgumentNullException()
    {
        Action act = () => SqsFifoMapper.ResolveMessageGroupId(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── ResolveOrGenerateDeduplicationId ──────────────────────────────────────

    [Fact]
    public void ResolveOrGenerateDeduplicationId_ExplicitHeaderPresent_ReturnsExplicitHeader()
    {
        var headers = new Dictionary<string, string>
        {
            [SqsHeaderMapper.DeduplicationIdHeader] = "explicit-dedup-id",
        };

        string? result = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", "body"u8, contentBasedDeduplication: false);

        result.Should().Be("explicit-dedup-id");
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_ContentBasedDeduplicationTrue_ReturnsNull()
    {
        var headers = new Dictionary<string, string>();

        string? result = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", "body"u8, contentBasedDeduplication: true);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_ContentBasedDeduplicationTrueWithExplicitHeader_ReturnsExplicitHeader()
    {
        // Explicit header always wins — even when content-based-dedup is enabled.
        var headers = new Dictionary<string, string>
        {
            [SqsHeaderMapper.DeduplicationIdHeader] = "explicit-wins",
        };

        string? result = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", "body"u8, contentBasedDeduplication: true);

        result.Should().Be("explicit-wins");
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_NoExplicitHeader_ReturnsDeterministicHash()
    {
        var headers = new Dictionary<string, string>();
        byte[] body = "hello world"u8.ToArray();

        string? result = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", body, contentBasedDeduplication: false);

        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_SameGroupAndBody_ReturnsSameHash()
    {
        // Deterministic: identical (group, body) → identical dedup id.
        var headers = new Dictionary<string, string>();
        byte[] body = "identical body"u8.ToArray();

        string? result1 = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-abc", body, contentBasedDeduplication: false);

        string? result2 = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-abc", body, contentBasedDeduplication: false);

        result1.Should().Be(result2);
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_DifferentBody_ReturnsDifferentHash()
    {
        var headers = new Dictionary<string, string>();

        string? result1 = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", "body-A"u8, contentBasedDeduplication: false);

        string? result2 = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", "body-B"u8, contentBasedDeduplication: false);

        result1.Should().NotBe(result2);
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_SameBodyDifferentGroup_ReturnsDifferentHash()
    {
        // Group id is included in the hash — identical bodies in different groups must differ.
        var headers = new Dictionary<string, string>();
        byte[] body = "same body"u8.ToArray();

        string? result1 = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-A", body, contentBasedDeduplication: false);

        string? result2 = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-B", body, contentBasedDeduplication: false);

        result1.Should().NotBe(result2);
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_GeneratedHash_LengthIsAtMost128Chars()
    {
        var headers = new Dictionary<string, string>();

        string? result = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", "some body content"u8, contentBasedDeduplication: false);

        result.Should().NotBeNull();
        result!.Length.Should().BeLessThanOrEqualTo(128,
            "SQS MessageDeduplicationId must not exceed 128 characters");
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_GeneratedHash_IsNonEmpty()
    {
        var headers = new Dictionary<string, string>();

        string? result = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", "body"u8, contentBasedDeduplication: false);

        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_NullMessageGroupId_StillGeneratesHash()
    {
        // Null group id is valid input — hash is still generated from body only.
        var headers = new Dictionary<string, string>();

        string? result = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, messageGroupId: null, "body"u8, contentBasedDeduplication: false);

        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_EmptyBody_GeneratesHash()
    {
        var headers = new Dictionary<string, string>();

        string? result = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            headers, "group-1", ReadOnlySpan<byte>.Empty, contentBasedDeduplication: false);

        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ResolveOrGenerateDeduplicationId_NullHeaders_ThrowsArgumentNullException()
    {
        Action act = () => SqsFifoMapper.ResolveOrGenerateDeduplicationId(
            null!, "group-1", "body"u8, contentBasedDeduplication: false);

        act.Should().Throw<ArgumentNullException>();
    }
}
