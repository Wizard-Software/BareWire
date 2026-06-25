using AwesomeAssertions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Bus;
using BareWire.Configuration;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Unit tests for <see cref="OrderingKeyResolver.ResolveTyped"/> (R8.13, ADR-026 §5 / M3): the typed-selector
/// seam that completes the key-source chain (explicit header/selector → correlation-id fallback → keyless).
/// <see cref="OrderingKeyResolver.ResolveTyped"/> is a documented seam with no runtime caller until R8.15+
/// (D1): the R8.6 fan-out hashes lanes on the RAW <c>InboundMessage</c> BEFORE deserialization, so resolving
/// a CLR-property selector here would require premature deserialization that violates ADR-003 (zero-copy).
/// These tests exercise the seam directly with an already-deserialized message object.
/// </summary>
/// <remarks>
/// The D3 SEC tests use a recognisable secret value and assert its ABSENCE (anti-tautology) across
/// <c>.Message</c>, <c>.StackTrace</c>, and <c>.ToString()</c>, plus <c>InnerException == null</c> (V2:
/// <see cref="System.Exception.StackTrace"/> does NOT recurse into the inner exception, so an inner-chaining
/// regression that re-attaches a PII-carrying inner would pass without the <c>BeNull()</c> guard).
/// </remarks>
public sealed class OrderingKeyResolverTypedTests
{
    private const string SecretKeyValue = "acct-secret-789";
    private const string SecretPayload = "ssn-555-66-7777";
    private const string CorrelationIdHeader = "correlation-id";

    private sealed record OrderTestMessage(string CustomerId);

    private sealed record OtherMessage(int Value);

    private static ConsumerOrderingConfiguration HeaderConfig(string headerName)
    {
        var cfg = new ConsumerOrderingConfiguration();
        cfg.ByHeader(headerName);
        return cfg;
    }

    private static ConsumerOrderingConfiguration SelectorConfig(Func<OrderTestMessage, object?> selector)
    {
        var cfg = new ConsumerOrderingConfiguration();
        cfg.By(selector);
        return cfg;
    }

    private static ConsumerOrderingConfiguration CorrelationIdConfig()
    {
        var cfg = new ConsumerOrderingConfiguration();
        cfg.ByCorrelationId();
        return cfg;
    }

    // ── (a) Header source takes precedence over the selector ──────────────────────────────────────

    [Fact]
    public void ResolveTyped_WithHeader_ReturnsHeaderValue()
    {
        ConsumerOrderingConfiguration cfg = HeaderConfig("ordering-key");
        var headers = new Dictionary<string, string> { ["ordering-key"] = "cust-42" };
        var message = new OrderTestMessage("ignored-because-header-wins");

        // Header takes the highest precedence even when a (separately configured) selector would project
        // a different value. Here the header path returns its value regardless of the message object.
        string? key = OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, message, headers);

        key.Should().Be("cust-42");
    }

    // ── (b) Typed selector — the active R8.13 member ──────────────────────────────────────────────

    [Fact]
    public void ResolveTyped_WithSelector_ReturnsProjectedKey()
    {
        ConsumerOrderingConfiguration cfg = SelectorConfig(m => m.CustomerId);
        var message = new OrderTestMessage("cust-99");

        string? key = OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, message, EmptyHeaders());

        key.Should().Be("cust-99");
    }

    [Fact]
    public void ResolveTyped_SelectorReturnsNull_TreatedAsKeyless()
    {
        // Heterogeneous stream: the selector legitimately returns null for some messages → keyless,
        // NOT an exception (documented allowed behaviour).
        ConsumerOrderingConfiguration cfg = SelectorConfig(_ => null);
        var message = new OrderTestMessage("any");

        string? key = OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, message, EmptyHeaders());

        key.Should().BeNull();
    }

    [Fact]
    public void ResolveTyped_HeterogeneousStream_WrongMessageType_TreatedAsKeyless()
    {
        // The configured selector reads OrderTestMessage, but the deserialized message is OtherMessage.
        // The resolver must NOT invoke the adapter (would throw InvalidCastException) — it treats the
        // type-mismatched message as keyless.
        ConsumerOrderingConfiguration cfg = SelectorConfig(m => m.CustomerId);
        var message = new OtherMessage(7);

        string? key = OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, message, EmptyHeaders());

        key.Should().BeNull();
    }

    [Fact]
    public void ResolveTyped_SelectorConfigured_NullDeserializedMessage_TreatedAsKeyless()
    {
        // No deserialized message available (e.g. raw passthrough) → the selector member cannot run; keyless.
        ConsumerOrderingConfiguration cfg = SelectorConfig(m => m.CustomerId);

        string? key = OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, deserializedMessage: null, EmptyHeaders());

        key.Should().BeNull();
    }

    // ── (c) Correlation-id fallback ───────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveTyped_CorrelationIdFallback_ReturnsCorrelationId()
    {
        ConsumerOrderingConfiguration cfg = CorrelationIdConfig();
        var headers = new Dictionary<string, string> { [CorrelationIdHeader] = "corr-123" };

        string? key = OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, deserializedMessage: null, headers);

        key.Should().Be("corr-123");
    }

    [Fact]
    public void ResolveTyped_CorrelationIdConfigured_HeaderAbsent_TreatedAsKeyless()
    {
        ConsumerOrderingConfiguration cfg = CorrelationIdConfig();

        string? key = OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, deserializedMessage: null, EmptyHeaders());

        key.Should().BeNull();
    }

    // ── (d) Keyless — no configured key source ────────────────────────────────────────────────────

    [Fact]
    public void ResolveTyped_Keyless_ReturnsNull()
    {
        var cfg = new ConsumerOrderingConfiguration(); // no source configured

        string? key = OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, deserializedMessage: null, EmptyHeaders());

        key.Should().BeNull();
    }

    // ── D3 (SEC-1): selector throwing → key-free BareWireException ─────────────────────────────────

    [Fact]
    public void ResolveTyped_SelectorThrows_ExceptionFreeOfPayloadAndKey()
    {
        // The selector throws an exception whose own message carries both a recognisable PII payload AND
        // the ordering-key value. The resolver MUST surface a BareWireException that contains NEITHER.
        ConsumerOrderingConfiguration cfg = SelectorConfig(m =>
            throw new InvalidOperationException($"boom payload={SecretPayload} key={SecretKeyValue}"));
        var message = new OrderTestMessage(SecretKeyValue);

        Action act = () => OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, message, EmptyHeaders());

        var ex = act.Should().Throw<BareWireException>().Which;

        ex.Message.Should().NotContain(SecretPayload, "payload must never reach the exception message (S1)");
        ex.Message.Should().NotContain(SecretKeyValue, "the key value must never reach the exception message (S1)");
        ex.StackTrace.Should().NotContain(SecretPayload);
        ex.StackTrace.Should().NotContain(SecretKeyValue);

        // V2: .StackTrace does NOT recurse into the inner exception; .ToString() does. Assert the full graph
        // is clean AND that no inner exception is attached (an inner would carry the user's PII-bearing
        // .Message/.StackTrace). BeNull() is the load-bearing guard against a future inner-chaining regression.
        ex.ToString().Should().NotContain(SecretPayload);
        ex.ToString().Should().NotContain(SecretKeyValue);
        ex.InnerException.Should().BeNull(
            "the key-free wrapper must not attach the original exception as inner — it would carry PII");
    }

    [Fact]
    public void ResolveTyped_SelectorThrowsTargetInvocation_Unwrapped_ExceptionFreeOfPayloadAndKey()
    {
        // Defensive: a future caller could surface a TargetInvocationException wrapping the user exception.
        // The resolver must still produce a key-free BareWireException (unwrap or not — never leak PII).
        ConsumerOrderingConfiguration cfg = SelectorConfig(m =>
            throw new System.Reflection.TargetInvocationException(
                new InvalidOperationException($"inner payload={SecretPayload} key={SecretKeyValue}")));
        var message = new OrderTestMessage(SecretKeyValue);

        Action act = () => OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, message, EmptyHeaders());

        var ex = act.Should().Throw<BareWireException>().Which;

        ex.Message.Should().NotContain(SecretPayload);
        ex.Message.Should().NotContain(SecretKeyValue);
        ex.ToString().Should().NotContain(SecretPayload);
        ex.ToString().Should().NotContain(SecretKeyValue);
        ex.InnerException.Should().BeNull();
    }

    // ── V1 (SEC-2): selector result whose ToString() throws → ToString() is UNDER the SEC guard ────

    [Fact]
    public void ResolveTyped_SelectorResultToStringThrows_ExceptionFreeOfPayloadAndKey()
    {
        // The selector returns an object whose ToString() throws carrying PII. Because stringification
        // happens INSIDE the try/catch (V1), that throw must be caught and surfaced key-free.
        ConsumerOrderingConfiguration cfg = SelectorConfig(_ => new ThrowingToString());
        var message = new OrderTestMessage(SecretKeyValue);

        Action act = () => OrderingKeyResolver.ResolveTyped(cfg, cfg.SelectorAdapter, message, EmptyHeaders());

        var ex = act.Should().Throw<BareWireException>().Which;

        ex.Message.Should().NotContain(SecretPayload);
        ex.Message.Should().NotContain(SecretKeyValue);
        ex.ToString().Should().NotContain(SecretPayload);
        ex.ToString().Should().NotContain(SecretKeyValue);
        ex.InnerException.Should().BeNull();
    }

    private sealed class ThrowingToString
    {
        public override string ToString() =>
            throw new InvalidOperationException($"tostring payload={SecretPayload} key={SecretKeyValue}");
    }

    private static Dictionary<string, string> EmptyHeaders() => new();
}
