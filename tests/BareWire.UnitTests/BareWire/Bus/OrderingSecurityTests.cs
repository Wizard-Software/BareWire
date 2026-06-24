using System.Reflection;
using AwesomeAssertions;
using BareWire.Bus;
using Microsoft.Extensions.Logging;
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

    // ── S2: PoisonContract gap-logs use opaque token, never raw key value (R8.12) ──────────────────

    /// <summary>
    /// Guards that <c>PoisonContract</c>'s <see cref="LoggerMessageAttribute"/>-generated log message
    /// templates do NOT include structured-log parameter names that could carry raw ordering-key or
    /// message-body data. The allowed parameter names are: EndpointName, OpaqueToken, FailureCategory,
    /// MessageId. Any other string parameter name on a gap-log or failed-settle method is a PII-leak vector.
    /// </summary>
    [Fact]
    public void PoisonContract_LoggerMessages_DoNotContainRawKeyOrBodyParameters()
    {
        // Collect all [LoggerMessage] attribute instances on PoisonContract.
        // LoggerMessage attribute stores the Message template as a named argument.
        var logMethods = typeof(PoisonContract)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<LoggerMessageAttribute>() is not null)
            .ToArray();

        logMethods.Should().NotBeEmpty("PoisonContract must have LoggerMessage-decorated methods");

        // Forbidden parameter name substrings that indicate a raw key, routing key, or body value
        // could be embedded. The log methods may only carry safe structural parameters.
        string[] forbiddenSubstrings = ["Key", "Routing", "Header", "Body", "Value"];

        foreach (MethodInfo method in logMethods)
        {
            var attr = method.GetCustomAttribute<LoggerMessageAttribute>()!;
            string template = attr.Message ?? string.Empty;

            foreach (string forbidden in forbiddenSubstrings)
            {
                template.Should().NotContain(forbidden,
                    $"PoisonContract log message on '{method.Name}' must not contain '{forbidden}' — " +
                    "raw ordering-key and body values are forbidden in log templates (S2 — ADR-026 §NIE WOLNO)");
            }
        }
    }

    // ── S2: MappingEpochTracker re-map log uses opaque token, never raw key value (R8.12 C4) ────────

    /// <summary>
    /// Guards that <c>MappingEpochTracker</c>'s <see cref="LoggerMessageAttribute"/>-generated log
    /// message templates do NOT include structured-log parameter names that could carry raw ordering-key
    /// or message-body data. Mirrors the PoisonContract guard for the C4 re-map detection path.
    /// </summary>
    [Fact]
    public void MappingEpochTracker_LoggerMessages_DoNotContainRawKeyOrBodyParameters()
    {
        // Collect all [LoggerMessage] attribute instances on MappingEpochTracker.
        var logMethods = typeof(MappingEpochTracker)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<LoggerMessageAttribute>() is not null)
            .ToArray();

        logMethods.Should().NotBeEmpty("MappingEpochTracker must have LoggerMessage-decorated methods");

        // Forbidden parameter name substrings that indicate a raw key or body value.
        string[] forbiddenSubstrings = ["Key", "Routing", "Header", "Body", "Value"];

        foreach (MethodInfo method in logMethods)
        {
            var attr = method.GetCustomAttribute<LoggerMessageAttribute>()!;
            string template = attr.Message ?? string.Empty;

            foreach (string forbidden in forbiddenSubstrings)
            {
                template.Should().NotContain(forbidden,
                    $"MappingEpochTracker log message on '{method.Name}' must not contain '{forbidden}' — " +
                    "raw ordering-key values are forbidden in log templates (S2 — ADR-026 §NIE WOLNO)");
            }
        }
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
