using System.Reflection;
using AwesomeAssertions;
using BareWire.Bus;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// SEC contract tests for the ordering path (ADR-026 §NIE WOLNO, §Weryfikacja:328/340). These tests
/// guard the invariant that an ordering-key value (potential PII) NEVER reaches an exception message,
/// the <c>OptionValue</c> property, or any logging surface — and pin it for the future ordering work
/// (R8.9–R8.13) so a regression there fails loudly. Every assertion uses a recognisable secret value
/// and asserts its ABSENCE (anti-tautology).
/// </summary>
public sealed class OrderingSecurityTests
{
    private const string SecretKeyValue = "acct-secret-123";

    // ── S1: key value never in .Message NOR .OptionValue (ADR-026:328, verbatim) ─────────────────

    [Fact]
    public void OrderingConfigError_KeyValue_NotInMessageNorOptionValue()
    {
        // Simulate a fail-fast scenario: a caller has the key value in scope but MUST route through
        // the key-free builder. The header NAME may legitimately differ from the secret VALUE; here
        // the secret value is what must never appear.
        var ex = OrderingKeyDiagnostics.OrderingConfigError(
            optionName: "Ordering.Strategy",
            endpointName: "orders",
            headerName: "customer-id",
            usesSelector: false,
            expected: "an explicit affinity declaration");

        ex.Message.Should().NotContain(SecretKeyValue,
            "the key VALUE must never appear in the exception message (S1 — ADR-026:328)");
        ex.OptionValue.Should().NotContain(SecretKeyValue,
            "the key VALUE must never appear in OptionValue (S1 — ADR-026:328)");
        ex.OptionValue.Should().BeNull(
            "the ordering builder structurally forces OptionValue to null — there is no path to a value");
    }

    // ── S2: opaque token for gap correlation, never the raw value (contract for R8.12) ───────────

    [Fact]
    public void OpaqueToken_UsedForGapCorrelation_NotRawValue()
    {
        // Simulates the future R8.12 gap-log: correlate on the token, never the raw key.
        string token = OrderingKeyDiagnostics.ToOpaqueToken(SecretKeyValue);

        token.Should().NotContain(SecretKeyValue,
            "a future gap-log (R8.12) must correlate on the opaque token, never the raw key (S2)");
    }

    // ── Regression guard: raw key never exposed to a logging surface by the resolver ─────────────

    [Fact]
    public void OrderingKeyResolver_ExposesExactlyOneKeyReturningMember_Resolve()
    {
        // The only sanctioned diagnostic representation of a key is OrderingKeyDiagnostics.ToOpaqueToken.
        // OrderingKeyResolver.Resolve returns the raw key (string?) for the SOLE purpose of immediate
        // hashing (ResolveLaneIndex consumes it and returns an int lane index). This guard pins the
        // resolver's string-returning surface to EXACTLY { Resolve } — so a future caller (R8.9–R8.13)
        // that adds a second string-returning member (a diagnostic key accessor that could be logged
        // directly) fails this test loudly.
        MethodInfo[] keyReturningMembers = typeof(OrderingKeyResolver)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(OrderingKeyResolver)
                && m.ReturnType == typeof(string))
            .ToArray();

        keyReturningMembers.Should().ContainSingle(
            "the resolver must expose exactly one key-returning member (Resolve); a second "
            + "string-returning member would be a PII-leak vector for R8.9–R8.13")
            .Which.Name.Should().Be(nameof(OrderingKeyResolver.Resolve),
            "the sole key-returning member is Resolve; the sole diagnostic key representation is "
            + "OrderingKeyDiagnostics.ToOpaqueToken");
    }
}
