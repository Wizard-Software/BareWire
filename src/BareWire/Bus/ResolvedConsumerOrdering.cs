using BareWire.Abstractions.Configuration;

namespace BareWire.Bus;

// Internal value type carrying the resolved ordering decision for a single receive endpoint.
// Created by ConsumerOrderingStrategyResolver.Resolve() and used as a stack-local assertion result;
// never passed to ReceiveEndpointRunner (resolver is validate-only, R8.11 OQ2).
internal readonly record struct ResolvedConsumerOrdering(
    ConsumerOrderingStrategy EffectiveStrategy,
    TransportAffinity EffectiveAffinity);
