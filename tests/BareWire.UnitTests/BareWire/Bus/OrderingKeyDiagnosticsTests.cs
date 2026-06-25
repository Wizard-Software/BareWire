using AwesomeAssertions;
using BareWire.Bus;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Unit tests for <see cref="OrderingKeyDiagnostics"/> — the single sanctioned point for touching
/// an ordering-key value in a diagnostic context (ADR-026 §NIE WOLNO, S1/S2). Every assertion uses
/// a recognisable secret value and asserts its ABSENCE in the output, so a leak fails the test
/// immediately (anti-tautology).
/// </summary>
public sealed class OrderingKeyDiagnosticsTests
{
    private const string SecretKey = "acct-secret-123";

    // ── S2: ToOpaqueToken ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToOpaqueToken_DoesNotContainRawKey()
    {
        string token = OrderingKeyDiagnostics.ToOpaqueToken(SecretKey);

        token.Should().NotContain(SecretKey,
            "the opaque correlation token must never embed the raw key value (S2)");
    }

    [Fact]
    public void ToOpaqueToken_IsStable_ForSameKey()
    {
        string first = OrderingKeyDiagnostics.ToOpaqueToken(SecretKey);
        string second = OrderingKeyDiagnostics.ToOpaqueToken(SecretKey);

        second.Should().Be(first,
            "the same key must always map to the same token — SHA-256 is deterministic, no random salt");
    }

    [Fact]
    public void ToOpaqueToken_DiffersForDifferentKeys()
    {
        string a = OrderingKeyDiagnostics.ToOpaqueToken("customer-42");
        string b = OrderingKeyDiagnostics.ToOpaqueToken("customer-43");

        b.Should().NotBe(a, "distinct keys must produce distinct tokens for correct log correlation");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("customer-42")]
    [InlineData("a-very-long-ordering-key-value-that-exceeds-the-stackalloc-threshold-by-a-wide-margin-"
        + "to-exercise-the-rented-buffer-path-padding-padding-padding-padding-padding-padding-padding-"
        + "padding-padding-padding-padding-padding-padding-padding-padding-padding-padding-padding-pad")]
    public void ToOpaqueToken_HasFixedLength_IndependentOfKeyLength(string key)
    {
        string token = OrderingKeyDiagnostics.ToOpaqueToken(key);

        token.Should().HaveLength(16,
            "the token is a truncated hash (8 bytes → 16 hex chars); its length must not depend on the "
            + "key length — proof of shortening, i.e. the token is not the raw value");
    }

    [Fact]
    public void ToOpaqueToken_NullKey_Throws()
    {
        Action act = () => OrderingKeyDiagnostics.ToOpaqueToken(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── S1: OrderingConfigError ─────────────────────────────────────────────────────────────────

    [Fact]
    public void OrderingConfigError_NeverEmbedsKeyValue_InOptionValue()
    {
        var ex = OrderingKeyDiagnostics.OrderingConfigError(
            optionName: "Ordering.Strategy",
            endpointName: "orders",
            headerName: "customer-id",
            usesSelector: false,
            expected: "an explicit affinity declaration");

        ex.OptionValue.Should().BeNull(
            "the key-free builder must structurally force OptionValue to null (S1 — ADR-026:328)");
    }

    [Fact]
    public void OrderingConfigError_SelectorRendersStablePlaceholder_NotValue()
    {
        var ex = OrderingKeyDiagnostics.OrderingConfigError(
            optionName: "Ordering.Strategy",
            endpointName: "orders",
            headerName: null,
            usesSelector: true,
            expected: "an explicit affinity declaration");

        ex.Message.Should().Contain(OrderingKeyDiagnostics.SelectorPlaceholder,
            "a selector must always render as the constant placeholder, never the evaluated value (S1)");
    }

    [Fact]
    public void OrderingConfigError_HeaderName_IsSafeToSurface()
    {
        // Header NAME (operator configuration) is safe to surface; header VALUE is not.
        var ex = OrderingKeyDiagnostics.OrderingConfigError(
            optionName: "Ordering.Strategy",
            endpointName: "orders",
            headerName: "customer-id",
            usesSelector: false,
            expected: "an explicit affinity declaration");

        ex.Message.Should().Contain("customer-id",
            "the configured header NAME is operator configuration, not message data — safe to surface "
            + "(ADR-026:317/340)");
    }
}
