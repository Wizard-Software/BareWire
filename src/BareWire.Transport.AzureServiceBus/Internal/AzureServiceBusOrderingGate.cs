using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// M2 gate: validates that an Azure Service Bus receive endpoint requesting transport-native
/// per-key ordering has sessions enabled on the adapter, preventing fail-OPEN scenarios where
/// the transport Sessions capability flag is declared but no session receiver is active.
/// </summary>
/// <remarks>
/// <para>
/// Without this gate, requesting <see cref="ConsumerOrderingStrategy.TransportNative"/> or
/// <see cref="ConsumerOrderingStrategy.Auto"/> on an Azure Service Bus endpoint that has sessions
/// disabled would silently fall through to the competing-consumers path
/// (<c>ConsumeNonSessionAsync</c>), which provides no FIFO-per-key guarantee. This constitutes a
/// fail-OPEN: the strategy is accepted but the ordering contract is not met.
/// </para>
/// <para>
/// This gate is a pure, stateless, broker-free function: the verdict is derived entirely from
/// two configuration values and is therefore reachable at startup without any network round-trip.
/// </para>
/// <para>
/// Call-site: this method is intended to be invoked by the consumer-ordering resolver (a
/// subsequent task) in the Azure Service Bus transport branch. Providing it here as a tested,
/// isolated primitive prevents it from becoming dead code while the resolver is not yet wired in.
/// </para>
/// </remarks>
internal static class AzureServiceBusOrderingGate
{
    /// <summary>
    /// The option name passed to <see cref="BareWireConfigurationException"/> when the gate
    /// fires. References the fluent knob that must be called (<c>UseSessions()</c>) — never
    /// an ordering-key value (S1 rule).
    /// </summary>
    internal const string SessionsOptionName = "AzureServiceBus.UseSessions";

    /// <summary>
    /// Ensures that Azure Service Bus session affinity is available for the requested ordering
    /// strategy. Throws <see cref="BareWireConfigurationException"/> when
    /// <see cref="ConsumerOrderingStrategy.TransportNative"/> or
    /// <see cref="ConsumerOrderingStrategy.Auto"/> is requested while sessions are disabled on
    /// the adapter (fail-OPEN prevention). No-op for
    /// <see cref="ConsumerOrderingStrategy.LocalPartitioned"/>.
    /// </summary>
    /// <param name="strategy">The consumer ordering strategy requested for the endpoint.</param>
    /// <param name="sessionsEnabled">
    /// <see langword="true"/> when <c>UseSessions()</c> was called on the
    /// <c>AzureServiceBusConfigurator</c> before <c>Build()</c>; otherwise
    /// <see langword="false"/>.
    /// </param>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <paramref name="strategy"/> requires transport-native session affinity but
    /// <paramref name="sessionsEnabled"/> is <see langword="false"/>. The exception message and
    /// <see cref="BareWireConfigurationException.OptionValue"/> contain only the strategy name —
    /// never any ordering-key value (S1 rule).
    /// </exception>
    internal static void EnsureSessionAffinityAvailable(
        ConsumerOrderingStrategy strategy,
        bool sessionsEnabled)
    {
        bool requiresTransportAffinity =
            strategy is ConsumerOrderingStrategy.TransportNative or ConsumerOrderingStrategy.Auto;

        if (requiresTransportAffinity && !sessionsEnabled)
        {
            // S1: optionValue carries only the strategy NAME — never a key or payload value.
            throw new BareWireConfigurationException(
                optionName: SessionsOptionName,
                optionValue: strategy.ToString(),
                expectedValue:
                    "Azure Service Bus per-key consumer ordering with strategy 'TransportNative' or 'Auto' " +
                    "requires sessions to be enabled via UseSessions(); the Sessions capability flag alone " +
                    "does not provide FIFO affinity (enable sessions on the adapter and create the queue " +
                    "with RequiresSession=true).");
        }

        // LocalPartitioned and any future non-affinity strategies fall through silently:
        // they do not rely on Azure Service Bus session receivers for their ordering guarantee.
    }
}
