namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The dead-letter exchange (and optional routing-key override) resolved for one source queue from its
/// <c>x-dead-letter-exchange</c> / <c>x-dead-letter-routing-key</c> arguments. Built once, for every
/// queue that declares a DLX, when the owning <see cref="InMemorySettlement"/> is constructed — see
/// <see cref="InMemorySettlement.BuildDeadLetterTargets"/>.
/// </summary>
/// <param name="Exchange">
/// The dead-letter exchange name. May be the empty string (the default exchange) — that is a valid
/// target, distinct from "no DLX declared" (the source queue simply has no entry in the target table).
/// </param>
/// <param name="RoutingKey">
/// The routing-key override from <c>x-dead-letter-routing-key</c>, or <see langword="null"/> when the
/// source queue declares no override — the delivery's original routing key is used instead.
/// </param>
internal readonly record struct DeadLetterTarget(string Exchange, string? RoutingKey);
