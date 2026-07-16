using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;

namespace BareWire.Transport.RabbitMQ.Configuration;

/// <summary>
/// OPT-IN, transport-specific (AMQP) topology helper for a consumer. Declares an exchange, a queue, and one
/// exchange-&gt;queue binding in a single call, routed through the transport adapter's topology-deployment path.
/// This helper lives in the RabbitMQ transport assembly — NOT in the zero-dependency abstractions façade — so
/// AMQP topology vocabulary never leaks onto the transport-agnostic consumer-configurator interface.
/// </summary>
public static class ConsumerConfiguratorTopologyExtensions
{
    /// <summary>
    /// Declares an exchange, a queue, and one exchange-&gt;queue binding for this consumer, in a single call.
    /// <para>
    /// OPT-IN: without this call no broker entity is created — the default manual-topology behaviour is
    /// unchanged. The declared entities flow through the transport adapter's topology-deployment path, so they
    /// are only applied to the broker when topology deployment is explicitly triggered.
    /// </para>
    /// <para>
    /// <paramref name="bindingKey"/> is the AMQP binding routing-key (broker-side) and is a SEPARATE, EXPLICIT
    /// axis from the consumer's dispatcher routing keys (<see cref="IConsumerConfigurator{TConsumer}.RoutingKey"/>):
    /// the two are never coupled. Setting a binding key here does not add a dispatcher routing key, and vice versa.
    /// </para>
    /// </summary>
    /// <typeparam name="TConsumer">The consumer implementation type.</typeparam>
    /// <typeparam name="TMessage">The message type the consumer handles.</typeparam>
    /// <param name="configurator">The consumer configurator to extend. Must not be <see langword="null"/>.</param>
    /// <param name="exchange">The exchange to declare. Must not be <see langword="null"/> or empty.</param>
    /// <param name="queue">The queue to declare. Must not be <see langword="null"/> or empty.</param>
    /// <param name="bindingKey">The AMQP binding routing-key (broker-side). Must not be <see langword="null"/>.</param>
    /// <param name="exchangeType">The exchange type to declare. Defaults to <see cref="ExchangeType.Topic"/>.</param>
    /// <param name="durable">Whether the declared exchange and queue survive a broker restart. Defaults to <see langword="true"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configurator"/> or <paramref name="bindingKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="exchange"/> or <paramref name="queue"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The configurator is a foreign implementation not produced by this transport.</exception>
    public static void DeclareTopology<TConsumer, TMessage>(
        this IConsumerConfigurator<TConsumer, TMessage> configurator,
        string exchange,
        string queue,
        string bindingKey,
        ExchangeType exchangeType = ExchangeType.Topic,
        bool durable = true)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(configurator);

        if (configurator is not ConsumerConfigurator<TConsumer, TMessage> concrete)
        {
            throw new InvalidOperationException(
                $"DeclareTopology requires the RabbitMQ transport consumer configurator, but received " +
                $"'{configurator.GetType().FullName}'. This helper is only supported on configurators produced " +
                $"by the RabbitMQ transport.");
        }

        concrete.DeclareConsumerTopology(exchange, queue, bindingKey, exchangeType, durable);
    }
}
