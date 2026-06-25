namespace BareWire.Abstractions.Topology;

/// <summary>
/// Provides a fluent API for declaring exchanges, queues, and bindings that form the message topology.
/// Declarations are accumulated during bus configuration and deployed to the broker via
/// <see cref="IBusControl.DeployTopologyAsync"/> (ADR-002: manual topology).
/// </summary>
public interface ITopologyConfigurator
{
    /// <summary>
    /// Declares an exchange on the broker with the specified routing characteristics.
    /// The declaration is idempotent — if the exchange already exists with compatible attributes,
    /// no error is raised.
    /// </summary>
    /// <param name="name">The exchange name.</param>
    /// <param name="type">The exchange type that controls message routing behaviour.</param>
    /// <param name="durable">
    /// <see langword="true"/> if the exchange should survive a broker restart; otherwise <see langword="false"/>.
    /// </param>
    /// <param name="autoDelete">
    /// <see langword="true"/> if the exchange should be deleted automatically when the last
    /// queue is unbound from it; otherwise <see langword="false"/>.
    /// </param>
    void DeclareExchange(string name, ExchangeType type, bool durable = true, bool autoDelete = false);

    /// <summary>
    /// Declares a queue on the broker.
    /// The declaration is idempotent — if the queue already exists with compatible attributes,
    /// no error is raised.
    /// </summary>
    /// <param name="name">The queue name.</param>
    /// <param name="durable">
    /// <see langword="true"/> if the queue should survive a broker restart; otherwise <see langword="false"/>.
    /// </param>
    /// <param name="autoDelete">
    /// <see langword="true"/> if the queue should be deleted when the last consumer disconnects;
    /// otherwise <see langword="false"/>.
    /// </param>
    /// <param name="arguments">
    /// Optional broker-specific queue arguments, such as <c>x-dead-letter-exchange</c> to route
    /// rejected messages to a dead-letter exchange, or <c>x-message-ttl</c> to set a per-queue
    /// message time-to-live. Pass <see langword="null"/> or omit when no arguments are required.
    /// </param>
    void DeclareQueue(string name, bool durable = true, bool autoDelete = false,
        IReadOnlyDictionary<string, object>? arguments = null);

    /// <summary>
    /// Declares a queue on the broker using a fluent configurator for queue arguments.
    /// The declaration is idempotent — if the queue already exists with compatible attributes,
    /// no error is raised.
    /// </summary>
    /// <param name="name">The queue name.</param>
    /// <param name="durable">
    /// <see langword="true"/> if the queue should survive a broker restart; otherwise <see langword="false"/>.
    /// </param>
    /// <param name="autoDelete">
    /// <see langword="true"/> if the queue should be deleted when the last consumer disconnects;
    /// otherwise <see langword="false"/>.
    /// </param>
    /// <param name="configure">
    /// Callback to configure queue arguments via <see cref="IQueueConfigurator"/>.
    /// </param>
    void DeclareQueue(string name, bool durable, bool autoDelete,
        Action<IQueueConfigurator> configure);

    /// <summary>
    /// Creates a binding that routes messages from <paramref name="exchange"/> to <paramref name="queue"/>
    /// when the message routing key matches <paramref name="routingKey"/>.
    /// </summary>
    /// <param name="exchange">The source exchange name.</param>
    /// <param name="queue">The destination queue name.</param>
    /// <param name="routingKey">The routing key pattern (e.g. <c>"order.created"</c> or <c>"#"</c>).</param>
    void BindExchangeToQueue(string exchange, string queue, string routingKey);

    /// <summary>
    /// Creates a binding that routes messages from <paramref name="source"/> exchange to
    /// <paramref name="destination"/> exchange when the routing key matches <paramref name="routingKey"/>.
    /// Used to build exchange fan-out and hierarchical routing topologies.
    /// </summary>
    /// <param name="source">The name of the source exchange.</param>
    /// <param name="destination">The name of the destination exchange.</param>
    /// <param name="routingKey">The routing key pattern to match against.</param>
    void BindExchangeToExchange(string source, string destination, string routingKey);

    /// <summary>
    /// Declares the per-type fanout exchange used for publish-style request/response.
    /// The exchange name is derived from the message type using the <c>Namespace:TypeName</c>
    /// convention (e.g. <c>MyApp.Messages:PaymentRequested</c>).
    /// The exchange is declared as durable, non-auto-delete, and of type <see cref="ExchangeType.Fanout"/>
    /// so that all bound responder queues receive every request.
    /// </summary>
    /// <typeparam name="T">
    /// The request message type. Must be a reference type. The type's CLR namespace and name
    /// determine the exchange name.
    /// </typeparam>
    void DeclareRequestExchange<T>() where T : class;

    /// <summary>
    /// Binds a responder queue to the per-type fanout request exchange with an empty routing key.
    /// Fanout exchanges ignore the routing key; it is set to <see cref="string.Empty"/> to satisfy
    /// the AMQP binding contract while making the intent explicit.
    /// </summary>
    /// <typeparam name="T">
    /// The request message type. Must be a reference type. The type's CLR namespace and name
    /// determine the exchange name via the <c>Namespace:TypeName</c> convention.
    /// </typeparam>
    /// <param name="queue">
    /// The name of the responder queue to bind. Must be non-null and non-empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="queue"/> is <see langword="null"/> or empty.
    /// </exception>
    void BindRequestExchangeToQueue<T>(string queue) where T : class;
}
