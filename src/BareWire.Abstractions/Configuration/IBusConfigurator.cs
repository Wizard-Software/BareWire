using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;

namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Provides the top-level fluent API for configuring a BareWire bus instance.
/// Passed to the configuration delegate registered with the DI container during application startup.
/// </summary>
public interface IBusConfigurator
{
    /// <summary>
    /// Configures the bus to use RabbitMQ as its transport layer.
    /// The <paramref name="configure"/> delegate receives an <see cref="IRabbitMqConfigurator"/>
    /// that provides a typed fluent API for host, topology, endpoint, and header-mapping settings.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives the <see cref="IRabbitMqConfigurator"/> and applies transport settings.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void UseRabbitMQ(Action<IRabbitMqConfigurator> configure);

    /// <summary>
    /// Configures observability (tracing, metrics, logging) for the bus.
    /// The <paramref name="configure"/> delegate receives an observability-specific configurator;
    /// the actual type is defined in <c>BareWire.Observability</c> and passed at runtime.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives the observability configurator object and applies settings.
    /// The parameter type is <see cref="object"/> to avoid a compile-time dependency on the
    /// observability package from within <c>BareWire.Abstractions</c>.
    /// </param>
    void ConfigureObservability(Action<object> configure);

    /// <summary>
    /// Adds a middleware component of type <typeparamref name="T"/> to the inbound message pipeline.
    /// Middleware is invoked in registration order for every message received by any endpoint on this bus.
    /// <typeparamref name="T"/> must be registered in the DI container.
    /// </summary>
    /// <typeparam name="T">
    /// The middleware implementation type. Must implement <see cref="IMessageMiddleware"/>.
    /// </typeparam>
    void AddMiddleware<T>() where T : class, IMessageMiddleware;

    /// <summary>
    /// Sets the primary message serializer for the bus.
    /// The serializer is used for all outbound typed message publishing unless overridden per-endpoint.
    /// <typeparamref name="T"/> must be registered in the DI container.
    /// </summary>
    /// <typeparam name="T">
    /// The serializer implementation type. Must implement <see cref="IMessageSerializer"/>.
    /// </typeparam>
    void UseSerializer<T>() where T : class, IMessageSerializer;

    /// <summary>
    /// Maps a message type to a specific <see cref="IMessageSerializer"/>, overriding the default
    /// serializer for publishing messages of type <typeparamref name="TMessage"/>.
    /// Required for publish-only bridge scenarios — for example, publishing in MassTransit envelope
    /// format to another system without declaring a receive endpoint.
    /// </summary>
    /// <typeparam name="TMessage">The message type to map. Must be a reference type.</typeparam>
    /// <typeparam name="TSerializer">
    /// The serializer implementation type. Must implement <see cref="IMessageSerializer"/>
    /// and be registered in the DI container (either explicitly via
    /// <c>services.AddSingleton&lt;TSerializer&gt;()</c> or via a dedicated helper such as
    /// <c>AddMassTransitEnvelopeSerializer()</c>).
    /// </typeparam>
    /// <remarks>
    /// This is a <strong>per-type</strong> override, not a global replacement of
    /// <see cref="IMessageSerializer"/>. Unmapped types continue using the default serializer
    /// (ADR-001 raw-first). Calling this method twice for the same
    /// <typeparamref name="TMessage"/> overwrites the previous mapping.
    /// <para>
    /// The mapping is bus-global and transport-agnostic — it applies to all
    /// <c>IBus.PublishAsync&lt;TMessage&gt;()</c> and <c>ISendEndpoint.SendAsync&lt;TMessage&gt;()</c>
    /// calls regardless of which transport is configured.
    /// </para>
    /// </remarks>
    void MapSerializer<TMessage, TSerializer>()
        where TMessage : class
        where TSerializer : class, IMessageSerializer;
}
