using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Headers;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.Transport.RabbitMQ.Configuration;

internal sealed class RabbitMqConfigurator : IRabbitMqConfigurator
{
    private string? _hostUri;
    private string? _defaultExchange;
    private bool _guaranteedRouting;
    private RabbitMqHostConfigurator? _hostConfigurator;
    private RabbitMqTopologyConfigurator? _topologyConfigurator;
    private RabbitMqHeaderMappingConfigurator? _headerMappingConfigurator;
    private readonly List<RabbitMqEndpointConfiguration> _endpoints = [];

    // Single config-time source of truth for per-type publish routing. Shared BY REFERENCE
    // with the lazily-created topology configurator (see ConfigureTopology) so MapExchange<T>,
    // MapRoutingKey<T>, DeclareExchange<T>, and Publish<T> all accumulate into ONE map set.
    private readonly PublishRegistry _publishRegistry = new();
    private readonly Dictionary<Type, PublishRequestRegistration> _publishRequestMappings = [];

    public void Host(string uri, Action<IHostConfigurator>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);

        _hostUri = uri;
        _hostConfigurator = new RabbitMqHostConfigurator();

        configure?.Invoke(_hostConfigurator);
    }

    public void DefaultExchange(string exchangeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(exchangeName);
        _defaultExchange = exchangeName;
    }

    public void GuaranteedRouting() => _guaranteedRouting = true;

    public void ConfigureTopology(Action<ITopologyConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _topologyConfigurator ??= new RabbitMqTopologyConfigurator(_publishRegistry);
        configure(_topologyConfigurator);
    }

    public void ReceiveEndpoint(string queueName, Action<IReceiveEndpointConfigurator> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentNullException.ThrowIfNull(configure);

        var endpoint = new RabbitMqEndpointConfiguration(queueName);
        configure(endpoint);
        _endpoints.Add(endpoint);
    }

    public void ConfigureHeaderMapping(Action<IHeaderMappingConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _headerMappingConfigurator ??= new RabbitMqHeaderMappingConfigurator();
        configure(_headerMappingConfigurator);
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

    public void PublishRequest<T>() where T : class =>
        _publishRequestMappings[typeof(T)] = new PublishRequestRegistration(
            ExchangeName: RequestExchangeNameFormatter.Format<T>(),
            Strict: false,
            AutoDeclare: false);

    public void PublishRequest<T>(Action<IPublishRequestOptions> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PublishRequestOptions();
        configure(options);

        string resolvedExchange = options.ExchangeName ?? RequestExchangeNameFormatter.Format<T>();

        _publishRequestMappings[typeof(T)] = new PublishRequestRegistration(
            ExchangeName: resolvedExchange,
            Strict: options.Strict,
            AutoDeclare: options.AutoDeclare);
    }

    private sealed class PublishRequestOptions : IPublishRequestOptions
    {
        public string? ExchangeName { get; set; }
        public bool Strict { get; set; }
        public bool AutoDeclare { get; set; }
    }

    internal RabbitMqTransportOptions Build()
    {
        ValidateUri(_hostUri);

        var options = new RabbitMqTransportOptions
        {
            ConnectionString = _hostUri!,
            EndpointConfigurations = _endpoints.ToArray(),
        };

        if (_defaultExchange is not null)
        {
            options.DefaultExchange = _defaultExchange;
        }

        options.GuaranteedRouting = _guaranteedRouting;

        if (_hostConfigurator is not null)
        {
            if (_hostConfigurator.UsernameValue is not null)
            {
                options.UsernameOverride = _hostConfigurator.UsernameValue;
            }

            if (_hostConfigurator.PasswordValue is not null)
            {
                options.PasswordOverride = _hostConfigurator.PasswordValue;
            }

            if (_hostConfigurator.TlsConfigure is not null)
            {
                options.ConfigureTls = _hostConfigurator.TlsConfigure;
            }
        }

        if (_topologyConfigurator is not null)
        {
            options.Topology = _topologyConfigurator.Build();
        }

        if (_headerMappingConfigurator is not null)
        {
            options.HeaderMappingConfigurator = _headerMappingConfigurator;
        }

        // SEC-4 snapshot invariant: hand the resolvers a defensive COPY, never the live config-time
        // PublishRegistry dictionaries. Build() takes a `new Dictionary<>(...)` clone so that any
        // post-Build mutation of the shared registry (or the registry being mutated by a still-held
        // configurator reference) cannot reach into the runtime ExchangeResolver / RoutingKeyResolver.
        if (_publishRegistry.RoutingKeyMappings.Count > 0)
        {
            options.RoutingKeyMappings = new Dictionary<Type, string>(_publishRegistry.RoutingKeyMappings);
        }

        if (_publishRegistry.ExchangeMappings.Count > 0)
        {
            ValidateExchangeMappings(options.Topology);
            options.ExchangeMappings = new Dictionary<Type, string>(_publishRegistry.ExchangeMappings);
        }

        // Snapshot the config-time divergence diagnostics (a defensive COPY, same SEC-4 reasoning).
        // The transport adapter emits these as DEFAULT-ON warnings at startup; they never affect
        // runtime resolution (last-call-wins already applied above).
        if (_publishRegistry.Divergences.Count > 0)
        {
            options.PublishRoutingDivergences = _publishRegistry.Divergences.ToArray();
        }

        if (_publishRequestMappings.Count > 0)
        {
            options.Topology = MergeAutoDeclareExchanges(options.Topology, _publishRequestMappings.Values);
            ValidatePublishRequestMappings(options.Topology);
            options.PublishRequestMappings =
                new Dictionary<Type, PublishRequestRegistration>(_publishRequestMappings);
        }

        return options;
    }

    private void ValidateExchangeMappings(TopologyDeclaration? topology)
    {
        if (topology is null)
        {
            throw new BareWireConfigurationException(
                optionName: "MapExchange",
                optionValue: null,
                expectedValue: "ConfigureTopology must be called before MapExchange<T>. " +
                               "Declare all exchanges via ConfigureTopology before mapping message types to them.");
        }

        HashSet<string> declaredExchanges = [..topology.Exchanges.Select(e => e.Name)];

        List<string>? missing = null;
        foreach (string exchangeName in _publishRegistry.ExchangeMappings.Values)
        {
            if (!declaredExchanges.Contains(exchangeName))
            {
                missing ??= [];
                missing.Add(exchangeName);
            }
        }

        if (missing is not null)
        {
            string missingList = string.Join(", ", missing.Distinct());
            throw new BareWireConfigurationException(
                optionName: "MapExchange",
                optionValue: missingList,
                expectedValue: "All exchanges referenced in MapExchange<T> must be declared via ConfigureTopology. " +
                               $"Missing exchanges: {missingList}.");
        }
    }

    // Fail-fast validation for publish-style request mappings. Called unconditionally from Build()
    // whenever the publish-request map is non-empty, independent of _exchangeMappings.Count
    // (ValidateExchangeMappings only runs inside `if (_exchangeMappings.Count > 0)` and never covers
    // this map). Entries with AutoDeclare == true are skipped: their exchange is declared on the deploy
    // path, so its absence from the explicit topology is expected. For every other entry the per-type
    // exchange must (1) be declared in the topology and (2) be of type Fanout — a Direct/Topic exchange
    // of the same name would silently break the broadcast to competing responders.
    private void ValidatePublishRequestMappings(TopologyDeclaration? topology)
    {
        // Only AutoDeclare == false entries require a pre-declared topology exchange.
        var requiresTopology = false;
        foreach (PublishRequestRegistration registration in _publishRequestMappings.Values)
        {
            if (!registration.AutoDeclare)
            {
                requiresTopology = true;
                break;
            }
        }

        if (!requiresTopology)
        {
            return;
        }

        if (topology is null)
        {
            throw new BareWireConfigurationException(
                optionName: "PublishRequest",
                optionValue: null,
                expectedValue: "ConfigureTopology must be called before PublishRequest<T>. " +
                               "Declare the per-type fanout exchange via ConfigureTopology, " +
                               "or set AutoDeclare = true to declare it on the deploy path.");
        }

        foreach (PublishRequestRegistration registration in _publishRequestMappings.Values)
        {
            if (registration.AutoDeclare)
            {
                continue;
            }

            string exchangeName = registration.ExchangeName;
            ExchangeDeclaration? declaration =
                topology.Exchanges.FirstOrDefault(e => e.Name == exchangeName);

            if (declaration is null)
            {
                throw new BareWireConfigurationException(
                    optionName: "PublishRequest",
                    optionValue: exchangeName,
                    expectedValue: "The per-type exchange referenced by PublishRequest<T> must be " +
                                   "declared via ConfigureTopology (or set AutoDeclare = true).");
            }

            if (declaration.Type != ExchangeType.Fanout)
            {
                throw new BareWireConfigurationException(
                    optionName: "PublishRequest",
                    optionValue: exchangeName,
                    expectedValue: $"The per-type exchange must be declared as ExchangeType.Fanout; " +
                                   $"'{declaration.Type}' would silently break the broadcast to " +
                                   "competing responders.");
            }
        }
    }

    // Merges per-type fanout exchange declarations for all AutoDeclare==true publish-request
    // registrations into the topology snapshot. Returns a NEW TopologyDeclaration (init-only record)
    // or creates one from scratch when topology is null. Exchanges already present by name are
    // skipped to guarantee idempotency (user may declare the exchange explicitly via the helper
    // AND set AutoDeclare=true).
    private static TopologyDeclaration MergeAutoDeclareExchanges(
        TopologyDeclaration? topology,
        IEnumerable<PublishRequestRegistration> registrations)
    {
        List<ExchangeDeclaration>? toAdd = null;

        HashSet<string> existing = topology is not null
            ? [..topology.Exchanges.Select(e => e.Name)]
            : [];

        foreach (PublishRequestRegistration registration in registrations)
        {
            if (!registration.AutoDeclare)
            {
                continue;
            }

            if (!existing.Add(registration.ExchangeName))
            {
                // Exchange already declared (either from topology or a prior iteration); skip.
                continue;
            }

            toAdd ??= [];
            toAdd.Add(new ExchangeDeclaration(registration.ExchangeName, ExchangeType.Fanout,
                Durable: true, AutoDelete: false));
        }

        if (toAdd is null)
        {
            // Nothing to merge — return the original snapshot (or an empty one if topology was null).
            return topology ?? new TopologyDeclaration();
        }

        IReadOnlyList<ExchangeDeclaration> mergedExchanges = topology is not null
            ? [..topology.Exchanges, ..toAdd]
            : [..toAdd];

        return topology is not null
            ? topology with { Exchanges = mergedExchanges }
            : new TopologyDeclaration { Exchanges = mergedExchanges };
    }

    private static void ValidateUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            throw new BareWireConfigurationException(
                optionName: "Host",
                optionValue: uri,
                expectedValue: "A RabbitMQ connection URI must be provided via Host(). " +
                               "Use amqp:// or amqps:// scheme (e.g. amqp://guest:guest@localhost:5672/).");
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme is not "amqp" and not "amqps"))
        {
            throw new BareWireConfigurationException(
                optionName: "Host",
                optionValue: uri,
                expectedValue: "The URI must use the amqp:// or amqps:// scheme " +
                               "(e.g. amqp://guest:guest@localhost:5672/).");
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            throw new BareWireConfigurationException(
                optionName: "Host",
                optionValue: uri,
                expectedValue: "The URI must contain a non-empty host name.");
        }
    }
}
