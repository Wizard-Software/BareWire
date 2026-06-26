using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;
using BareWire.Transport.RabbitMQ.Topology;

namespace BareWire.Transport.RabbitMQ;

/// <summary>
/// Accumulates topology declarations (exchanges, queues, bindings) and produces an immutable
/// <see cref="TopologyDeclaration"/> snapshot via <see cref="Build"/>.
/// </summary>
internal sealed class RabbitMqTopologyConfigurator : ITopologyConfigurator
{
    private readonly List<ExchangeDeclaration> _exchanges = [];
    private readonly List<QueueDeclaration> _queues = [];
    private readonly List<ExchangeQueueBinding> _exchangeQueueBindings = [];
    private readonly List<ExchangeExchangeBinding> _exchangeExchangeBindings = [];

    // Shared BY REFERENCE with the owning RabbitMqConfigurator so DeclareExchange<T> write-through
    // lands in the SAME per-type map set as MapExchange<T> / MapRoutingKey<T> / Publish<T> — one
    // source of truth, no merge step. A standalone instance (e.g. topology-only deploy paths)
    // gets a private registry: its DeclareExchange<T> mappings are simply not consumed.
    private readonly PublishRegistry _publishRegistry;

    internal RabbitMqTopologyConfigurator(PublishRegistry? publishRegistry = null) =>
        _publishRegistry = publishRegistry ?? new PublishRegistry();

    /// <inheritdoc />
    public void DeclareExchange(string name, ExchangeType type, bool durable = true, bool autoDelete = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        _exchanges.Add(new ExchangeDeclaration(name, type, durable, autoDelete));
    }

    /// <inheritdoc />
    public void DeclareExchange<T>(string name, ExchangeType type, bool durable = true,
        bool autoDelete = false, string? routingKey = null) where T : class
    {
        // (1) Declare the topology exactly like the non-generic overload (guards name).
        DeclareExchange(name, type, durable, autoDelete);

        // (2) Write-through the per-type mapping into the shared store: exchange = name always;
        //     routing key only when supplied (null preserves the typeof(T).FullName fallback).
        _publishRegistry.MapExchange(typeof(T), name);
        if (routingKey is not null)
        {
            _publishRegistry.MapRoutingKey(typeof(T), routingKey);
        }
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

        var configurator = new QueueConfigurator();
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
    public void DeclareRequestExchange<T>() where T : class =>
        DeclareExchange(RequestExchangeNameFormatter.Format<T>(), ExchangeType.Fanout, durable: true, autoDelete: false);

    /// <inheritdoc />
    public void BindRequestExchangeToQueue<T>(string queue) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(queue);
        BindExchangeToQueue(RequestExchangeNameFormatter.Format<T>(), queue, routingKey: string.Empty);
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
