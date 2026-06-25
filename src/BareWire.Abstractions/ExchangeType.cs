namespace BareWire.Abstractions;

/// <summary>
/// Specifies the AMQP exchange type used when declaring or binding an exchange.
/// </summary>
public enum ExchangeType
{
    /// <summary>
    /// Routes messages to queues whose binding key exactly matches the routing key.
    /// </summary>
    Direct,

    /// <summary>
    /// Routes messages to all bound queues regardless of routing key.
    /// </summary>
    Fanout,

    /// <summary>
    /// Routes messages to queues based on wildcard matching of the routing key against binding patterns.
    /// </summary>
    Topic,

    /// <summary>
    /// Routes messages based on header attributes rather than routing keys.
    /// </summary>
    Headers,

    /// <summary>
    /// Routes messages to one of the bound queues based on a consistent hash of the routing key,
    /// guaranteeing that messages sharing the same routing key are always routed to the same bound
    /// queue (for a fixed set of bound queues and weights). Each bound queue is assigned a weight via
    /// its binding routing key, and the broker distributes the hash space proportionally to the weights.
    /// </summary>
    /// <remarks>
    /// Requires the RabbitMQ <c>rabbitmq_consistent_hash_exchange</c> plugin. Declaring an exchange of
    /// this type against a broker without the plugin enabled raises a
    /// <see cref="Exceptions.BareWireTransportException"/> at deploy time. Consistent-hash exchanges are
    /// the transport-level building block for per-key consumer ordering: the same routing key always
    /// maps to the same queue, giving key-to-consumer affinity across competing consumers.
    /// </remarks>
    ConsistentHash,
}
