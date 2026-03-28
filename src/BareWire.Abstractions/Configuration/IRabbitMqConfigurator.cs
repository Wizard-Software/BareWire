using BareWire.Abstractions.Headers;
using BareWire.Abstractions.Topology;

namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Provides a typed fluent API for configuring the RabbitMQ transport layer on a BareWire bus.
/// Obtained via <see cref="IBusConfigurator.UseRabbitMQ"/> during application startup.
/// </summary>
public interface IRabbitMqConfigurator
{
    /// <summary>
    /// Configures the RabbitMQ broker host connection.
    /// Must be called at least once before the bus is started.
    /// </summary>
    /// <param name="uri">
    /// The RabbitMQ connection URI. Must use the <c>amqp://</c> or <c>amqps://</c> scheme
    /// (e.g. <c>amqp://guest:guest@localhost:5672/</c>).
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="configure">
    /// An optional delegate to configure host-level settings such as credentials and TLS.
    /// When <see langword="null"/>, credentials embedded in the URI are used.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="uri"/> is <see langword="null"/> or empty.
    /// </exception>
    void Host(string uri, Action<IHostConfigurator>? configure = null);

    /// <summary>
    /// Configures the AMQP topology (exchanges, queues, bindings) that will be deployed
    /// to the broker when <c>IBusControl.DeployTopologyAsync</c> is called.
    /// Per ADR-002, topology must be declared and deployed explicitly.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives an <see cref="ITopologyConfigurator"/> and declares the topology.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void ConfigureTopology(Action<ITopologyConfigurator> configure);

    /// <summary>
    /// Registers a receive endpoint (queue consumer) on the bus.
    /// Multiple calls accumulate endpoints — each <paramref name="queueName"/> becomes
    /// an independent consumer binding.
    /// </summary>
    /// <param name="queueName">
    /// The name of the queue to consume from. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="configure">
    /// A delegate that receives an <see cref="IReceiveEndpointConfigurator"/> and applies
    /// consumer, concurrency, and retry settings for this endpoint.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="queueName"/> is <see langword="null"/> or empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void ReceiveEndpoint(string queueName, Action<IReceiveEndpointConfigurator> configure);

    /// <summary>
    /// Sets the default exchange name used by <c>PublishAsync</c> when no <c>BW-Exchange</c>
    /// header is present on the outbound message.
    /// Per ADR-002, this must match an exchange declared via <see cref="ConfigureTopology"/>.
    /// </summary>
    /// <param name="exchangeName">
    /// The exchange name. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="exchangeName"/> is <see langword="null"/> or empty.
    /// </exception>
    void DefaultExchange(string exchangeName);

    /// <summary>
    /// Configures the mapping between BareWire canonical header names and RabbitMQ
    /// transport-specific header names. Use this to integrate with services that use
    /// non-standard or legacy header conventions.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives an <see cref="IHeaderMappingConfigurator"/> and applies
    /// the desired mappings.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void ConfigureHeaderMapping(Action<IHeaderMappingConfigurator> configure);

    /// <summary>
    /// Maps a message type to an explicit AMQP routing key used by <c>PublishAsync&lt;T&gt;</c>.
    /// Required when using topic exchanges with pattern-based bindings (e.g. <c>order.*</c>),
    /// because the default routing key (<c>typeof(T).FullName</c>) does not match such patterns.
    /// </summary>
    /// <typeparam name="T">The message type to map.</typeparam>
    /// <param name="routingKey">
    /// The routing key to use when publishing messages of type <typeparamref name="T"/>.
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="routingKey"/> is <see langword="null"/> or empty.
    /// </exception>
    void MapRoutingKey<T>(string routingKey) where T : class;
}
