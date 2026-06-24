using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;

namespace BareWire.Bus;

// Stateless, broker-free, deterministic resolver for ConsumerOrderingStrategy.
// Called once per receive endpoint during BareWireBusControl.StartAsync (startup validation only).
// Throws BareWireConfigurationException when no guaranteed ordering path is declared,
// or when the declared intent contradicts the transport's reported capabilities.
// Never reads ordering-key values — only enum names and operator-supplied config strings (S1 rule).
internal static class ConsumerOrderingStrategyResolver
{
    // The option name passed to BareWireConfigurationException — a constant that names the
    // configuration knob, not an ordering-key value (S1 rule).
    internal const string OrderingStrategyOptionName = "ConsumerOrdering.Strategy";

    // Resolves the consumer ordering strategy for a single endpoint at bus startup.
    // Returns a ResolvedConsumerOrdering on success. Throws BareWireConfigurationException
    // when no safe ordering path can be determined from the declared configuration.
    //
    // Rule precedence (per ADR-026 §3/§4 and plan §5.2 / R6):
    //   1. LocalPartitioned  — pass through without transport inspection.
    //   2. Auto / TransportNative:
    //      a. Sessions flag present  → fail-fast (Core never honors bare Sessions pre-R2.2, D1).
    //      b. OrderingKeys flag      → TransportNative with affinity None (Kafka/Pub-Sub partition).
    //      c. Neither                → resolve from declared TransportAffinity (RabbitMQ path).
    //         SAC / ConsistentHash   → TransportNative with that affinity.
    //         None                   → fail-fast (no guaranteed path declared).
    //   TransportNative additionally:
    //      Contradiction check: if a declarative affinity (SAC/ConsistentHash) is declared but
    //      the transport reports OrderingKeys, the intent contradicts the capability → fail-fast (D4).
    internal static ResolvedConsumerOrdering Resolve(
        IConsumerOrderingConfiguration ordering,
        TransportCapabilities capabilities,
        string transportName,
        string endpointName)
    {
        ConsumerOrderingStrategy strategy = ordering.Strategy;

        // Rule 1: LocalPartitioned is an explicit opt-in with single-instance semantics.
        // Passed through unconditionally — no transport validation applies.
        if (strategy == ConsumerOrderingStrategy.LocalPartitioned)
        {
            return new ResolvedConsumerOrdering(
                ConsumerOrderingStrategy.LocalPartitioned,
                ordering.TransportAffinity);
        }

        // Rule 2: Auto / TransportNative — capability-driven resolution.

        // Rule 2a: Sessions capability present → fail-fast (Core does NOT honor bare Sessions
        // pre-R2.2; this is intentionally broader than AzureServiceBusOrderingGate, per D1/R1).
        if (capabilities.HasFlag(TransportCapabilities.Sessions))
        {
            throw new BareWireConfigurationException(
                optionName: OrderingStrategyOptionName,
                optionValue: strategy.ToString(),
                expectedValue:
                    $"Consumer ordering strategy '{strategy}' on endpoint '{endpointName}' (transport " +
                    $"'{transportName}') requires Azure Service Bus session affinity, which is " +
                    "gated until R2.2. Session-receiver support is not yet available. " +
                    "Use LocalPartitioned for single-instance ordering, or wait for R2.2.");
        }

        // Rule 2b: OrderingKeys flag → transport-native partition affinity (Kafka / Pub-Sub).
        // Declarative TransportAffinity is not used on this path (affinity comes from the partition).
        if (capabilities.HasFlag(TransportCapabilities.OrderingKeys))
        {
            // Rule 2b + D4 contradiction check for TransportNative:
            // If the caller declared a declarative affinity (SAC/ConsistentHash) but the transport
            // reports partition-level OrderingKeys, the intent and capability contradict each other.
            if (strategy == ConsumerOrderingStrategy.TransportNative
                && ordering.TransportAffinity is TransportAffinity.SingleActiveConsumer
                    or TransportAffinity.ConsistentHash)
            {
                throw new BareWireConfigurationException(
                    optionName: OrderingStrategyOptionName,
                    optionValue: strategy.ToString(),
                    expectedValue:
                        $"Consumer ordering strategy 'TransportNative' with affinity " +
                        $"'{ordering.TransportAffinity}' on endpoint '{endpointName}' (transport " +
                        $"'{transportName}') contradicts the transport's OrderingKeys capability. " +
                        "Remove the explicit TransportAffinity declaration, or switch to a transport " +
                        "that uses declarative affinity (e.g. RabbitMQ).");
            }

            // Auto and TransportNative both normalize to TransportNative on success.
            return new ResolvedConsumerOrdering(ConsumerOrderingStrategy.TransportNative, TransportAffinity.None);
        }

        // Rule 2c: No Sessions, no OrderingKeys — resolve from declared TransportAffinity (RabbitMQ path).
        // Both Auto and TransportNative normalize to TransportNative on success.
        return ordering.TransportAffinity switch
        {
            TransportAffinity.SingleActiveConsumer or TransportAffinity.ConsistentHash =>
                new ResolvedConsumerOrdering(ConsumerOrderingStrategy.TransportNative, ordering.TransportAffinity),

            // Rule 2c / None → fail-fast: no transport-native path declared AND no explicit
            // LocalPartitioned consent. Never silently degrade (ADR-026 alt. C rejected).
            _ => throw new BareWireConfigurationException(
                optionName: OrderingStrategyOptionName,
                optionValue: strategy.ToString(),
                expectedValue:
                    $"Consumer ordering strategy '{strategy}' on endpoint '{endpointName}' (transport " +
                    $"'{transportName}') requires a declared ordering path. The transport has neither " +
                    "Sessions nor OrderingKeys capabilities; declare TransportAffinity.SingleActiveConsumer " +
                    "or TransportAffinity.ConsistentHash, or switch to ConsumerOrderingStrategy.LocalPartitioned " +
                    "for single-instance-only ordering."),
        };
    }
}
