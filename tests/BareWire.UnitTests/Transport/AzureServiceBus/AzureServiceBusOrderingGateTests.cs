using AwesomeAssertions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.AzureServiceBus.Internal;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

public sealed class AzureServiceBusOrderingGateTests
{
    // ── Throwing cases (strategy requires transport affinity, sessions disabled) ─

    [Theory]
    [InlineData(ConsumerOrderingStrategy.TransportNative)]
    [InlineData(ConsumerOrderingStrategy.Auto)]
    public void EnsureSessionAffinityAvailable_AffinityStrategyWithoutSessions_Throws(
        ConsumerOrderingStrategy strategy)
    {
        Action act = () =>
            AzureServiceBusOrderingGate.EnsureSessionAffinityAvailable(strategy, sessionsEnabled: false);

        act.Should().Throw<BareWireConfigurationException>();
    }

    // ── Non-throwing cases (sessions enabled) ────────────────────────────────────

    [Theory]
    [InlineData(ConsumerOrderingStrategy.TransportNative)]
    [InlineData(ConsumerOrderingStrategy.Auto)]
    public void EnsureSessionAffinityAvailable_AffinityStrategyWithSessions_DoesNotThrow(
        ConsumerOrderingStrategy strategy)
    {
        Action act = () =>
            AzureServiceBusOrderingGate.EnsureSessionAffinityAvailable(strategy, sessionsEnabled: true);

        act.Should().NotThrow();
    }

    // ── LocalPartitioned: bypasses the gate entirely (no sessions involvement) ───

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureSessionAffinityAvailable_LocalPartitioned_DoesNotThrow(bool sessionsEnabled)
    {
        Action act = () =>
            AzureServiceBusOrderingGate.EnsureSessionAffinityAvailable(
                ConsumerOrderingStrategy.LocalPartitioned, sessionsEnabled);

        act.Should().NotThrow();
    }

    // ── S1: exception message/value must not leak any ordering-key value ─────────

    // ── S1: exception message/value must not leak any ordering-key value ─────────

    [Theory]
    [InlineData(ConsumerOrderingStrategy.TransportNative)]
    [InlineData(ConsumerOrderingStrategy.Auto)]
    public void EnsureSessionAffinityAvailable_Throws_DoesNotLeakKeyValue(
        ConsumerOrderingStrategy strategy)
    {
        // S1 sentinel — a hypothetical ordering-key value that must never appear in the exception.
        // Chosen to be disjoint from strategy names / option names so NotContain cannot false-pass.
        const string HypotheticalKey = "customer-42";

        var ex = Assert.Throws<BareWireConfigurationException>(() =>
            AzureServiceBusOrderingGate.EnsureSessionAffinityAvailable(
                strategy, sessionsEnabled: false));

        // S1: no key value in message or OptionValue.
        ex.Message.Should().NotContain(HypotheticalKey,
            because: "ordering-key values must never appear in exception messages (S1 rule)");
        ex.OptionValue.Should().NotContain(HypotheticalKey,
            because: "ordering-key values must never appear in OptionValue (S1 rule)");

        // The message must refer to the configuration knob so the operator knows what to fix.
        ex.Message.Should().Contain("UseSessions",
            because: "the exception must guide the operator to the missing configuration knob");
    }
}
