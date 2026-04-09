using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Headers;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.RabbitMQ.Configuration;

internal sealed class RabbitMqConfigurator : IRabbitMqConfigurator
{
    private string? _hostUri;
    private string? _defaultExchange;
    private RabbitMqHostConfigurator? _hostConfigurator;
    private RabbitMqTopologyConfigurator? _topologyConfigurator;
    private RabbitMqHeaderMappingConfigurator? _headerMappingConfigurator;
    private readonly List<RabbitMqEndpointConfiguration> _endpoints = [];
    private readonly Dictionary<Type, string> _routingKeyMappings = [];
    private readonly Dictionary<Type, string> _exchangeMappings = [];

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

    public void ConfigureTopology(Action<ITopologyConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _topologyConfigurator ??= new RabbitMqTopologyConfigurator();
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
        _routingKeyMappings[typeof(T)] = routingKey;
    }

    public void MapExchange<T>(string exchangeName) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(exchangeName);
        _exchangeMappings[typeof(T)] = exchangeName;
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

        if (_routingKeyMappings.Count > 0)
        {
            options.RoutingKeyMappings = new Dictionary<Type, string>(_routingKeyMappings);
        }

        if (_exchangeMappings.Count > 0)
        {
            ValidateExchangeMappings(options.Topology);
            options.ExchangeMappings = new Dictionary<Type, string>(_exchangeMappings);
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
        foreach (string exchangeName in _exchangeMappings.Values)
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
