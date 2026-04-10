namespace BareWire.Abstractions.Topology;

/// <summary>
/// Fluent API for configuring queue arguments when declaring a queue via
/// <see cref="ITopologyConfigurator.DeclareQueue(string, bool, bool, System.Action{IQueueConfigurator}?)"/>.
/// Convenience methods cover the most common RabbitMQ queue arguments; use
/// <see cref="Argument"/> for any argument not covered by a dedicated method.
/// </summary>
public interface IQueueConfigurator
{
    /// <summary>
    /// Routes rejected or expired messages to the specified dead-letter exchange.
    /// Maps to <c>x-dead-letter-exchange</c>.
    /// </summary>
    /// <param name="exchangeName">The name of the dead-letter exchange.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="exchangeName"/> is <see langword="null"/> or empty.</exception>
    IQueueConfigurator DeadLetterExchange(string exchangeName);

    /// <summary>
    /// Overrides the routing key used when dead-lettering messages.
    /// Maps to <c>x-dead-letter-routing-key</c>.
    /// </summary>
    /// <param name="routingKey">The routing key for dead-lettered messages.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="routingKey"/> is <see langword="null"/>.</exception>
    IQueueConfigurator DeadLetterRoutingKey(string routingKey);

    /// <summary>
    /// Sets the per-queue message time-to-live. Messages older than this are dead-lettered
    /// or discarded. Maps to <c>x-message-ttl</c> (converted to milliseconds).
    /// </summary>
    /// <param name="ttl">
    /// The time-to-live duration. Must be positive and not exceed approximately 24.85 days
    /// (<see cref="int.MaxValue"/> milliseconds), which is the RabbitMQ broker limit.
    /// </param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// <paramref name="ttl"/> is zero, negative, or exceeds <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    IQueueConfigurator MessageTtl(TimeSpan ttl);

    /// <summary>
    /// Sets the maximum number of messages the queue can hold.
    /// Maps to <c>x-max-length</c>.
    /// </summary>
    /// <param name="maxMessages">The maximum message count. Must be positive.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="maxMessages"/> is zero or negative.</exception>
    IQueueConfigurator MaxLength(long maxMessages);

    /// <summary>
    /// Sets the maximum total size in bytes of all messages in the queue.
    /// Maps to <c>x-max-length-bytes</c>.
    /// </summary>
    /// <param name="maxBytes">The maximum total size in bytes. Must be positive.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="maxBytes"/> is zero or negative.</exception>
    IQueueConfigurator MaxLengthBytes(long maxBytes);

    /// <summary>
    /// Sets the queue type (Classic, Quorum, Stream).
    /// Maps to <c>x-queue-type</c>.
    /// </summary>
    /// <param name="type">The desired queue type.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    IQueueConfigurator SetQueueType(QueueType type);

    /// <summary>
    /// Sets the overflow strategy when <see cref="MaxLength"/> or <see cref="MaxLengthBytes"/> is reached.
    /// Maps to <c>x-overflow</c>. Default (when not set): <see cref="OverflowStrategy.DropHead"/>.
    /// </summary>
    /// <param name="strategy">The overflow strategy to apply.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    IQueueConfigurator Overflow(OverflowStrategy strategy);

    /// <summary>
    /// Sets an arbitrary broker-specific queue argument not covered by convenience methods.
    /// Use for <c>x-expires</c>, <c>x-max-priority</c>, etc.
    /// </summary>
    /// <param name="key">The argument key (e.g. <c>"x-max-priority"</c>).</param>
    /// <param name="value">The argument value.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    IQueueConfigurator Argument(string key, object value);
}
