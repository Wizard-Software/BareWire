using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Topology;
using BareWire.Transport.InMemory.Topology;

namespace BareWire.Transport.InMemory.Configuration;

internal sealed class InMemoryConfigurator : IInMemoryConfigurator
{
    private string? _defaultExchange;
    private bool _guaranteedRouting;
    private bool _autoDeclareEndpointQueues;
    private int? _queueCapacity;
    private TimeSpan? _sendTimeout;
    private int? _maxMessageSize;
    private int? _maxRedeliveries;
    private TimeSpan? _drainTimeout;
    private bool _deferEnabled;
    private TimeSpan? _deferDelay;
    private InMemoryTopologyConfigurator? _topologyConfigurator;
    private readonly List<InMemoryEndpointConfiguration> _endpoints = [];

    // Single config-time source of truth for per-type publish routing. Shared BY REFERENCE
    // with the lazily-created topology configurator (see ConfigureTopology) so MapExchange<T>,
    // MapRoutingKey<T>, DeclareExchange<T>, and Publish<T> all accumulate into ONE map set.
    private readonly PublishRegistry _publishRegistry = new();

    public void ConfigureTopology(Action<ITopologyConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _topologyConfigurator ??= new InMemoryTopologyConfigurator(_publishRegistry);
        configure(_topologyConfigurator);
    }

    public void ReceiveEndpoint(string queueName, Action<IReceiveEndpointConfigurator> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentNullException.ThrowIfNull(configure);

        var endpoint = new InMemoryEndpointConfiguration(queueName);
        configure(endpoint);
        _endpoints.Add(endpoint);
    }

    public void DefaultExchange(string exchangeName)
    {
        ArgumentNullException.ThrowIfNull(exchangeName);
        _defaultExchange = exchangeName;
    }

    public void GuaranteedRouting() => _guaranteedRouting = true;

    public void AutoDeclareEndpointQueues() => _autoDeclareEndpointQueues = true;

    // Scalar knobs do NOT validate eagerly — the contract (IInMemoryConfigurator) validates them "at
    // bus startup". Validation happens in InMemoryTransportOptions.Validate(), called from Build().
    public void QueueCapacity(int capacity) => _queueCapacity = capacity;

    public void SendTimeout(TimeSpan timeout) => _sendTimeout = timeout;

    public void MaxMessageSize(int bytes) => _maxMessageSize = bytes;

    public void MaxRedeliveries(int maxRedeliveries) => _maxRedeliveries = maxRedeliveries;

    public void DrainTimeout(TimeSpan timeout) => _drainTimeout = timeout;

    public void EnableDefer(TimeSpan? delay = null)
    {
        _deferEnabled = true;
        _deferDelay = delay ?? InMemoryTransportOptions.DefaultDeferDelay;
    }

    public void MapRoutingKey<T>(string routingKey) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(routingKey);
        _publishRegistry.MapRoutingKey(typeof(T), routingKey);
    }

    public void MapExchange<T>(string exchangeName) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(exchangeName);
        _publishRegistry.MapExchange(typeof(T), exchangeName);
    }

    public void Publish<T>(Action<IPublishConfigurator<T>> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Write-through to the shared registry; last-call-wins per T across every shape.
        var publishConfigurator = new PublishConfigurator<T>(_publishRegistry);
        configure(publishConfigurator);
    }

    internal InMemoryTransportOptions Build()
    {
        var options = new InMemoryTransportOptions
        {
            EndpointConfigurations = _endpoints.ToArray(),
        };

        if (_defaultExchange is not null)
        {
            options.DefaultExchange = _defaultExchange;
        }

        options.GuaranteedRouting = _guaranteedRouting;
        options.AutoDeclareEndpointQueues = _autoDeclareEndpointQueues;

        if (_queueCapacity is { } queueCapacity)
        {
            options.QueueCapacity = queueCapacity;
        }

        if (_sendTimeout is { } sendTimeout)
        {
            options.SendTimeout = sendTimeout;
        }

        if (_maxMessageSize is { } maxMessageSize)
        {
            options.MaxMessageSize = maxMessageSize;
        }

        if (_maxRedeliveries is { } maxRedeliveries)
        {
            options.MaxRedeliveries = maxRedeliveries;
        }

        if (_drainTimeout is { } drainTimeout)
        {
            options.DrainTimeout = drainTimeout;
        }

        options.DeferEnabled = _deferEnabled;

        if (_deferDelay is { } deferDelay)
        {
            options.DeferDelay = deferDelay;
        }

        if (_topologyConfigurator is not null)
        {
            options.Topology = _topologyConfigurator.Build();
        }

        // Defensive COPY, never the live config-time PublishRegistry dictionaries — a post-Build
        // mutation of the shared registry must not reach into the runtime resolvers.
        if (_publishRegistry.RoutingKeyMappings.Count > 0)
        {
            options.RoutingKeyMappings = new Dictionary<Type, string>(_publishRegistry.RoutingKeyMappings);
        }

        if (_publishRegistry.ExchangeMappings.Count > 0)
        {
            options.ExchangeMappings = new Dictionary<Type, string>(_publishRegistry.ExchangeMappings);
        }

        if (_publishRegistry.Divergences.Count > 0)
        {
            options.PublishRoutingDivergences = _publishRegistry.Divergences.ToArray();
        }

        // Merge opt-in consumer topology fragments into the accumulated topology, so the
        // exchange/queue/binding entries flow through the same path as ConfigureTopology. Purely
        // additive and disjoint from the publish-side mappings above.
        TopologyDeclaration[] consumerTopologyFragments =
            [.. _endpoints.SelectMany(static e => e.ConsumerTopologyFragments)];
        if (consumerTopologyFragments.Length > 0)
        {
            options.Topology = MergeConsumerTopology(options.Topology, consumerTopologyFragments);
        }

        options.Validate();
        return options;
    }

    /// <summary>
    /// Concatenates opt-in consumer topology fragments onto an optional base topology, returning a NEW
    /// <see cref="TopologyDeclaration"/> with the four entity lists concatenated. Duplicate names are
    /// harmless — topology deployment is idempotent. The base topology (from
    /// <see cref="ConfigureTopology"/>) is preserved verbatim when present.
    /// </summary>
    private static TopologyDeclaration MergeConsumerTopology(
        TopologyDeclaration? topology,
        IReadOnlyList<TopologyDeclaration> fragments)
    {
        IReadOnlyList<ExchangeDeclaration> baseExchanges = topology?.Exchanges ?? [];
        IReadOnlyList<QueueDeclaration> baseQueues = topology?.Queues ?? [];
        IReadOnlyList<ExchangeQueueBinding> baseExchangeQueueBindings = topology?.ExchangeQueueBindings ?? [];
        IReadOnlyList<ExchangeExchangeBinding> baseExchangeExchangeBindings = topology?.ExchangeExchangeBindings ?? [];

        IReadOnlyList<ExchangeDeclaration> exchanges =
            [.. baseExchanges, .. fragments.SelectMany(static f => f.Exchanges)];
        IReadOnlyList<QueueDeclaration> queues =
            [.. baseQueues, .. fragments.SelectMany(static f => f.Queues)];
        IReadOnlyList<ExchangeQueueBinding> exchangeQueueBindings =
            [.. baseExchangeQueueBindings, .. fragments.SelectMany(static f => f.ExchangeQueueBindings)];
        IReadOnlyList<ExchangeExchangeBinding> exchangeExchangeBindings =
            [.. baseExchangeExchangeBindings, .. fragments.SelectMany(static f => f.ExchangeExchangeBindings)];

        return topology is not null
            ? topology with
            {
                Exchanges = exchanges,
                Queues = queues,
                ExchangeQueueBindings = exchangeQueueBindings,
                ExchangeExchangeBindings = exchangeExchangeBindings,
            }
            : new TopologyDeclaration
            {
                Exchanges = exchanges,
                Queues = queues,
                ExchangeQueueBindings = exchangeQueueBindings,
                ExchangeExchangeBindings = exchangeExchangeBindings,
            };
    }
}
