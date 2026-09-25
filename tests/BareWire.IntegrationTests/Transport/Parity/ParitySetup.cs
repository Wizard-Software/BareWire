using BareWire.Abstractions.Topology;

namespace BareWire.IntegrationTests.Transport.Parity;

/// <summary>
/// Transport-neutral description of one semantic-parity scenario: the topology to deploy plus the
/// handful of transport options a scenario needs to opt into (bounded queue capacity, guaranteed
/// routing, deferred redelivery, a redelivery limit). A concrete transport factory
/// (<see cref="TransportSemanticParityTests.CreateAdapterAsync"/>) turns this into a real
/// <c>ITransportAdapter</c> using whatever options type that transport actually has.
/// </summary>
/// <param name="Topology">The topology to deploy before the scenario runs.</param>
public sealed record ParitySetup(TopologyDeclaration Topology)
{
    /// <summary>
    /// Maps a queue name to the maximum number of messages it may hold before further sends are
    /// rejected. Empty when the scenario does not need a bounded queue. Every entry contributes to a
    /// single capacity applied transport-wide on the in-memory transport (see
    /// <c>InMemoryTransportSemanticParityTests</c>), so a scenario using this must keep every other
    /// queue it declares empty at the moment the bound matters.
    /// </summary>
    public IReadOnlyDictionary<string, int> BoundedQueues { get; init; } = new Dictionary<string, int>();

    /// <summary>Whether the adapter should be built with guaranteed (mandatory) routing enabled.</summary>
    public bool GuaranteedRouting { get; init; }

    /// <summary>The opt-in deferred-redelivery configuration, or <see langword="null"/> to leave it off.</summary>
    public ParityDefer? Defer { get; init; }

    /// <summary>The redelivery limit for <c>Requeue</c>, or <see langword="null"/> to use the transport's default.</summary>
    public int? MaxRedeliveries { get; init; }
}

/// <summary>Describes the opt-in deferred-redelivery configuration for one source queue.</summary>
/// <param name="SourceQueue">The queue deferred deliveries are redelivered back onto.</param>
/// <param name="Delay">The delay before a deferred delivery is redelivered.</param>
public sealed record ParityDefer(string SourceQueue, TimeSpan Delay);

/// <summary>
/// Rows of the "differences vs RabbitMQ" behavior table a transport may legitimately skip, each backed
/// by exactly one difference test in <see cref="TransportSemanticParityTests"/>.
/// </summary>
public enum ParityDifference
{
    /// <summary>Whether a message survives an adapter restart while the broker keeps running.</summary>
    Durability,

    /// <summary>The broker's overflow policy when a dead-letter queue itself is full.</summary>
    FullDeadLetterQueue,

    /// <summary><c>Requeue</c> past a redelivery limit on a queue type with no delivery-limit concept.</summary>
    UnboundedRequeue,

    /// <summary><c>Requeue</c> past a redelivery limit on a queue type that enforces one.</summary>
    BoundedRequeue,

    /// <summary>Whether a publisher-supplied <c>BW-*</c> header can reach the consumer through an explicit transport header mapping.</summary>
    BwHeaderStrip,

    /// <summary>Whether topology can be declared after the adapter has already started.</summary>
    RuntimeTopology,

    /// <summary>Whether a headers exchange and TTL / max-length queue arguments are accepted.</summary>
    HeadersExchangeTtlMaxLength,

    /// <summary>Whether a consumer outside its permitted virtual host is denied.</summary>
    Confidentiality,
}
