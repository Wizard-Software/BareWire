using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Headers;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.RabbitMQ.Configuration;

internal sealed class RabbitMqConfigurator : IRabbitMqConfigurator
{
    private string? _hostUri;
    private RabbitMqHostConfigurator? _hostConfigurator;
    private RabbitMqTopologyConfigurator? _topologyConfigurator;
    private RabbitMqHeaderMappingConfigurator? _headerMappingConfigurator;
    private readonly List<RabbitMqEndpointConfiguration> _endpoints = [];

    public void Host(string uri, Action<IHostConfigurator>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);

        _hostUri = uri;
        _hostConfigurator = new RabbitMqHostConfigurator();

        configure?.Invoke(_hostConfigurator);
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

    internal RabbitMqTransportOptions Build()
    {
        ValidateUri(_hostUri);

        var options = new RabbitMqTransportOptions
        {
            ConnectionString = _hostUri!,
            EndpointConfigurations = _endpoints.ToArray(),
        };

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

        return options;
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
