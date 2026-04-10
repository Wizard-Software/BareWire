using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
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

    /// <inheritdoc />
    public void DeclareExchange(string name, ExchangeType type, bool durable = true, bool autoDelete = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        _exchanges.Add(new ExchangeDeclaration(name, type, durable, autoDelete));
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
