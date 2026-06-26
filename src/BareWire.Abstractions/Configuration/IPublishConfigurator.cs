namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Configures per-type publish routing (target exchange and routing key) for message
/// type <typeparamref name="T"/> on the <c>PublishAsync&lt;T&gt;</c> path, as a single
/// grouped, discoverable block. An instance is passed to the delegate supplied to the
/// grouped <c>Publish&lt;T&gt;</c> configuration block on the RabbitMQ configurator.
/// This is an ergonomic sugar layer over the lower-level
/// <see cref="IRabbitMqConfigurator.MapExchange{T}"/> and
/// <see cref="IRabbitMqConfigurator.MapRoutingKey{T}"/> primitives — it does not replace
/// them and feeds the same per-type mapping set (single source of truth).
/// Scope is the <c>PublishAsync&lt;T&gt;</c> path only; point-to-point send is out of scope.
/// </summary>
/// <remarks>
/// Methods return <see langword="void"/> by design, matching the house configurator
/// convention (see <see cref="IReceiveEndpointConfigurator"/>) — settings are applied
/// imperatively inside the delegate rather than fluently chained.
/// </remarks>
/// <typeparam name="T">The message type to configure publish routing for. Must be a reference type.</typeparam>
public interface IPublishConfigurator<T>
    where T : class
{
    /// <summary>
    /// Maps message type <typeparamref name="T"/> to a specific target AMQP exchange used by
    /// <c>PublishAsync&lt;T&gt;</c>. Parity with <see cref="IRabbitMqConfigurator.MapExchange{T}"/> —
    /// prefer this grouped form when configuring the exchange and routing key for the same type
    /// together; use <see cref="IRabbitMqConfigurator.MapExchange{T}"/> for the low-level primitive.
    /// The exchange must be declared in the topology; a missing declaration fails fast at bus
    /// startup with <see cref="Exceptions.BareWireConfigurationException"/>.
    /// </summary>
    /// <param name="exchangeName">
    /// The exchange name. Must not be <see langword="null"/> or empty.
    /// </param>
    void Exchange(string exchangeName);

    /// <summary>
    /// Maps message type <typeparamref name="T"/> to an explicit AMQP routing key used by
    /// <c>PublishAsync&lt;T&gt;</c>. Parity with <see cref="IRabbitMqConfigurator.MapRoutingKey{T}"/>.
    /// Required for topic exchanges with pattern bindings, because the default routing key
    /// (<c>typeof(T).FullName</c>) does not match such patterns.
    /// </summary>
    /// <param name="routingKey">
    /// The routing key. Must not be <see langword="null"/> or empty.
    /// </param>
    void RoutingKey(string routingKey);

    // Reserved for a future per-type serializer override (out of scope here):
    //   void UseSerializer<TSerializer>() where TSerializer : Serialization.IMessageSerializer;
    // Intentionally declared as a comment only — do NOT implement in this task.
}
