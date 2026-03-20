namespace BareWire.Abstractions.Topology;

/// <summary>
/// Holds the complete set of topology declarations (exchanges, queues, and bindings) collected
/// during bus configuration and submitted to the transport via
/// <c>ITransportAdapter.DeployTopologyAsync</c>.
/// </summary>
/// <remarks>
/// Instances are built by <c>ITopologyConfigurator</c> and are immutable once passed to the transport.
/// The transport adapter is responsible for interpreting and applying the declarations idempotently.
/// </remarks>
public sealed record TopologyDeclaration
{
    /// <summary>
    /// Gets the ordered list of exchange declarations to be created or verified on the broker.
    /// </summary>
    public IReadOnlyList<ExchangeDeclaration> Exchanges { get; init; } = [];

    /// <summary>
    /// Gets the ordered list of queue declarations to be created or verified on the broker.
    /// </summary>
    public IReadOnlyList<QueueDeclaration> Queues { get; init; } = [];

    /// <summary>
    /// Gets the ordered list of exchange-to-queue bindings to be established on the broker.
    /// </summary>
    public IReadOnlyList<ExchangeQueueBinding> ExchangeQueueBindings { get; init; } = [];

    /// <summary>
    /// Gets the ordered list of exchange-to-exchange bindings to be established on the broker.
    /// </summary>
    public IReadOnlyList<ExchangeExchangeBinding> ExchangeExchangeBindings { get; init; } = [];
}

/// <summary>
/// Describes a single AMQP exchange to be declared on the broker.
/// </summary>
/// <param name="Name">The name of the exchange.</param>
/// <param name="Type">The exchange type that controls routing behaviour.</param>
/// <param name="Durable">Whether the exchange survives a broker restart.</param>
/// <param name="AutoDelete">Whether the exchange is deleted when the last queue is unbound.</param>
public sealed record ExchangeDeclaration(string Name, ExchangeType Type, bool Durable = true, bool AutoDelete = false);

/// <summary>
/// Describes a single AMQP queue to be declared on the broker.
/// </summary>
/// <param name="Name">The name of the queue.</param>
/// <param name="Durable">Whether the queue survives a broker restart.</param>
/// <param name="Exclusive">Whether the queue is exclusive to the declaring connection.</param>
/// <param name="AutoDelete">Whether the queue is deleted when the last consumer disconnects.</param>
/// <param name="Arguments">
/// Optional broker-specific queue arguments, such as <c>x-dead-letter-exchange</c> to route
/// rejected messages to a dead-letter exchange, or <c>x-message-ttl</c> to set a per-queue
/// message time-to-live. Pass <see langword="null"/> when no arguments are required.
/// </param>
public sealed record QueueDeclaration(
    string Name,
    bool Durable = true,
    bool Exclusive = false,
    bool AutoDelete = false,
    IReadOnlyDictionary<string, object>? Arguments = null);

/// <summary>
/// Describes a binding between an exchange and a queue.
/// </summary>
/// <param name="ExchangeName">The name of the source exchange.</param>
/// <param name="QueueName">The name of the destination queue.</param>
/// <param name="RoutingKey">The routing key pattern to match against.</param>
public sealed record ExchangeQueueBinding(string ExchangeName, string QueueName, string RoutingKey);

/// <summary>
/// Describes a binding between two exchanges (exchange-to-exchange routing).
/// </summary>
/// <param name="SourceExchangeName">The name of the source exchange.</param>
/// <param name="DestinationExchangeName">The name of the destination exchange.</param>
/// <param name="RoutingKey">The routing key pattern to match against.</param>
public sealed record ExchangeExchangeBinding(string SourceExchangeName, string DestinationExchangeName, string RoutingKey);
