using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.RabbitMQ.Configuration;
using RabbitMQ.Client;

namespace BareWire.Transport.RabbitMQ;

internal sealed class RabbitMqTransportOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public bool AutomaticRecoveryEnabled { get; set; } = true;

    public TimeSpan NetworkRecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);

    public string DefaultExchange { get; set; } = string.Empty;

    /// <summary>
    /// Optional username override. When set, overrides any username embedded in <see cref="ConnectionString"/>.
    /// </summary>
    public string? UsernameOverride { get; set; }

    /// <summary>
    /// Optional password override. When set, overrides any password embedded in <see cref="ConnectionString"/>.
    /// Never logged or included in diagnostic output.
    /// </summary>
    public string? PasswordOverride { get; set; }

    /// <summary>
    /// Fluent TLS configuration callback. When set, takes precedence over <see cref="SslOptions"/>.
    /// </summary>
    public Action<ITlsConfigurator>? ConfigureTls { get; set; }

    /// <summary>
    /// Raw <see cref="SslOption"/> for backward compatibility.
    /// Prefer <see cref="ConfigureTls"/> for new code.
    /// </summary>
    public SslOption? SslOptions { get; set; }

    /// <summary>
    /// Maximum time to wait when establishing a connection to the RabbitMQ broker.
    /// Applied as a timeout on <see cref="System.Threading.SemaphoreSlim.WaitAsync(TimeSpan)"/>
    /// in the request-client factory. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool DeferEnabled { get; set; }

    public int DeferDelayMs { get; set; } = 30_000;

    /// <summary>
    /// The accumulated topology declaration produced by <see cref="IRabbitMqConfigurator.ConfigureTopology"/>.
    /// <see langword="null"/> when no topology was configured.
    /// </summary>
    public TopologyDeclaration? Topology { get; set; }

    /// <summary>
    /// The accumulated receive endpoint configurations produced by
    /// <see cref="IRabbitMqConfigurator.ReceiveEndpoint"/>.
    /// </summary>
    public IReadOnlyList<RabbitMqEndpointConfiguration> EndpointConfigurations { get; set; } = [];

    /// <summary>
    /// The header mapping configurator produced by <see cref="IRabbitMqConfigurator.ConfigureHeaderMapping"/>.
    /// <see langword="null"/> when no custom header mapping was configured.
    /// </summary>
    public RabbitMqHeaderMappingConfigurator? HeaderMappingConfigurator { get; set; }

    /// <summary>
    /// Explicit routing key mappings produced by <see cref="IRabbitMqConfigurator.MapRoutingKey{T}"/>.
    /// <see langword="null"/> when no custom routing key mappings were configured.
    /// </summary>
    public IReadOnlyDictionary<Type, string>? RoutingKeyMappings { get; set; }

    /// <summary>
    /// Explicit type→exchange mappings produced by <see cref="IRabbitMqConfigurator.MapExchange{T}"/>.
    /// <see langword="null"/> when no explicit exchange mappings were configured.
    /// </summary>
    public IReadOnlyDictionary<Type, string>? ExchangeMappings { get; set; }

    public void Validate()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            throw new BareWireConfigurationException(
                optionName: nameof(ConnectionString),
                optionValue: ConnectionString,
                expectedValue: "A non-empty RabbitMQ connection string (e.g. amqp://guest:guest@localhost:5672/)");
        }
    }
}
