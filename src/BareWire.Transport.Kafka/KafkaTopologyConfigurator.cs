using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.Kafka.Topology;

namespace BareWire.Transport.Kafka;

/// <summary>
/// Accumulates topology declarations (exchanges, queues, bindings) and produces an immutable
/// <see cref="TopologyDeclaration"/> snapshot via <see cref="Build"/>.
/// </summary>
/// <remarks>
/// In Kafka, only queues (topics) are actionable at deploy time.
/// Exchanges and bindings are accepted by this configurator (to satisfy the shared
/// <see cref="ITopologyConfigurator"/> contract) but are silently ignored during
/// <c>KafkaTransportAdapter.DeployTopologyAsync</c> — Kafka has no exchange/binding concept.
/// Topic-specific parameters (partitions, retention) are supplied via
/// <c>QueueDeclaration.Arguments</c> using the <c>bw.kafka.*</c> key convention
/// (see <c>KafkaTopologyArguments</c>).
/// </remarks>
internal sealed class KafkaTopologyConfigurator : ITopologyConfigurator
{
    private readonly List<ExchangeDeclaration> _exchanges = [];
    private readonly List<QueueDeclaration> _queues = [];
    private readonly List<ExchangeQueueBinding> _exchangeQueueBindings = [];
    private readonly List<ExchangeExchangeBinding> _exchangeExchangeBindings = [];

    /// <inheritdoc />
    public void DeclareExchange(string name, ExchangeType type, bool durable = true, bool autoDelete = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        _exchanges.Add(new ExchangeDeclaration(name, type, durable, autoDelete));
    }

    /// <inheritdoc />
    /// <remarks>
    /// In Kafka, exchanges have no runtime meaning and there is no per-type publish-routing store
    /// to write through to, so this overload only records the exchange declaration (silently ignored
    /// at deploy time, like the non-generic overload). The <paramref name="routingKey"/> is not mapped.
    /// This method satisfies the <see cref="ITopologyConfigurator"/> contract.
    /// </remarks>
    public void DeclareExchange<T>(string name, ExchangeType type, bool durable = true,
        bool autoDelete = false, string? routingKey = null) where T : class
    {
        _ = routingKey;
        DeclareExchange(name, type, durable, autoDelete);
    }

    /// <inheritdoc />
    public void DeclareQueue(string name, bool durable = true, bool autoDelete = false,
        IReadOnlyDictionary<string, object>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        _queues.Add(new QueueDeclaration(name, durable, Exclusive: false, autoDelete, arguments));
    }

    /// <inheritdoc />
    public void DeclareQueue(string name, bool durable, bool autoDelete,
        Action<IQueueConfigurator> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new KafkaQueueConfigurator();
        configure(configurator);

        _queues.Add(new QueueDeclaration(name, durable, Exclusive: false, autoDelete, configurator.Build()));
    }

    /// <inheritdoc />
    public void BindExchangeToQueue(string exchange, string queue, string routingKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(exchange);
        ArgumentException.ThrowIfNullOrEmpty(queue);
        ArgumentNullException.ThrowIfNull(routingKey);

        _exchangeQueueBindings.Add(new ExchangeQueueBinding(exchange, queue, routingKey));
    }

    /// <inheritdoc />
    public void BindExchangeToExchange(string source, string destination, string routingKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(routingKey);

        _exchangeExchangeBindings.Add(new ExchangeExchangeBinding(source, destination, routingKey));
    }

    /// <inheritdoc />
    /// <remarks>
    /// In Kafka, exchanges have no runtime meaning and are silently ignored during deployment.
    /// This method satisfies the <see cref="ITopologyConfigurator"/> contract.
    /// </remarks>
    public void DeclareRequestExchange<T>() where T : class =>
        DeclareExchange($"{typeof(T).Namespace}:{typeof(T).Name}", ExchangeType.Fanout, durable: true, autoDelete: false);

    /// <inheritdoc />
    /// <remarks>
    /// In Kafka, exchange-to-queue bindings have no runtime meaning and are silently ignored during deployment.
    /// This method satisfies the <see cref="ITopologyConfigurator"/> contract.
    /// </remarks>
    public void BindRequestExchangeToQueue<T>(string queue) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(queue);
        BindExchangeToQueue($"{typeof(T).Namespace}:{typeof(T).Name}", queue, routingKey: string.Empty);
    }

    /// <summary>
    /// Builds an immutable <see cref="TopologyDeclaration"/> from the accumulated declarations.
    /// The configurator may be reused after calling <see cref="Build"/>.
    /// </summary>
    /// <returns>A snapshot of all declared exchanges, queues, and bindings.</returns>
    public TopologyDeclaration Build() =>
        new()
        {
            Exchanges = _exchanges.ToArray(),
            Queues = _queues.ToArray(),
            ExchangeQueueBindings = _exchangeQueueBindings.ToArray(),
            ExchangeExchangeBindings = _exchangeExchangeBindings.ToArray(),
        };
}
