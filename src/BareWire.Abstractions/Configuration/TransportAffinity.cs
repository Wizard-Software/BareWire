namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Declared transport-native affinity intent for a receive endpoint. Read from BareWire configuration
/// at startup (no broker round-trip) so <see cref="ConsumerOrderingStrategy.Auto"/> and
/// <see cref="ConsumerOrderingStrategy.TransportNative"/> can fail fast deterministically for transports
/// (notably RabbitMQ) that expose no introspectable ordering capability flag.
/// </summary>
/// <remarks>
/// The library enforces only that the intent is <em>declared</em>; whether the deployed broker topology
/// actually satisfies it is the operator's responsibility.
/// </remarks>
public enum TransportAffinity
{
    /// <summary>
    /// No transport-native affinity declared (default). Under <see cref="ConsumerOrderingStrategy.Auto"/>
    /// or <see cref="ConsumerOrderingStrategy.TransportNative"/> on a transport without an introspectable
    /// capability, this leads to a fail-fast at startup.
    /// </summary>
    None,

    /// <summary>
    /// Single-active-consumer affinity: at most one consumer is active per bound queue, giving ordered
    /// delivery with no in-queue parallelism. Declaratively consistent with
    /// <c>IQueueConfigurator.SingleActiveConsumer</c> (RabbitMQ <c>x-single-active-consumer</c>).
    /// </summary>
    SingleActiveConsumer,

    /// <summary>
    /// Consistent-hash affinity: the same routing key always maps to the same bound queue, giving
    /// key-to-consumer affinity with cross-key parallelism. Declaratively consistent with
    /// <c>ExchangeType.ConsistentHash</c> (RabbitMQ consistent-hash exchange). Carries a key-order loss
    /// window on re-map (queue add/remove or node restart re-hashes keys).
    /// </summary>
    ConsistentHash,
}
