using System.Collections.Immutable;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>The outcome of resolving a message's target queues for a given (exchange, routing key) pair.</summary>
internal enum InMemoryRouteStatus
{
    /// <summary>At least one queue was resolved; <see cref="InMemoryRouteResult.Queues"/> is non-empty.</summary>
    Routed,

    /// <summary>
    /// The exchange is declared (or is the default exchange), but no binding matched the routing key.
    /// </summary>
    Unroutable,

    /// <summary>The exchange is neither the default exchange nor declared in the topology.</summary>
    ExchangeNotFound,

    /// <summary>The routing key exceeds 255 UTF-8 bytes, the AMQP limit for a binding key.</summary>
    RoutingKeyTooLong,
}

/// <summary>
/// The result of <see cref="InMemoryRouter.Route"/>: a status plus the ordered, deduplicated set of
/// target queue names. <see cref="Queues"/> is empty for every status other than
/// <see cref="InMemoryRouteStatus.Routed"/>.
/// </summary>
/// <param name="Status">The routing outcome.</param>
/// <param name="Queues">
/// The target queue names, sorted with <see cref="StringComparer.Ordinal"/> and deduplicated — one
/// entry per queue, regardless of how many bindings (direct or reached through exchange-to-exchange
/// routing) matched it. Backed by an immutable array shared across calls with the same cache key, so
/// callers must not assume per-call identity but may rely on the content never mutating.
/// </param>
internal readonly record struct InMemoryRouteResult(InMemoryRouteStatus Status, ImmutableArray<string> Queues)
{
    /// <summary>Gets whether <see cref="Status"/> is <see cref="InMemoryRouteStatus.Routed"/>.</summary>
    internal bool IsRouted => Status == InMemoryRouteStatus.Routed;
}
