using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Topology;

namespace BareWire.InMemory.Internal;

/// <summary>
/// Forwards every <see cref="IInMemoryConfigurator"/> call to the transport's own configurator
/// unchanged, while separately recording the last <see cref="DrainTimeout"/> value passed by the
/// caller (last call wins — the same semantics as the transport's own configurator).
/// </summary>
/// <remarks>
/// The transport's own <c>InMemoryTransportOptions</c> is <see langword="internal"/> to
/// <c>BareWire.Transport.InMemory</c> and not visible to this bundle, so the bundle cannot read
/// the configured drain timeout back out of the transport options after
/// <c>AddBareWireInMemory</c> builds them. This decorator captures the value at configuration
/// time instead, purely from the public <see cref="IInMemoryConfigurator"/> surface, so that
/// <c>AddBareWireWithInMemory</c> can pass the same value on to the core's
/// <c>BusShutdownOptions</c>.
/// </remarks>
/// <param name="inner">The transport's own configurator that every call is forwarded to.</param>
/// <param name="capture">The shared capture instance that records the last drain timeout.</param>
internal sealed class DrainTimeoutCapturingConfigurator(IInMemoryConfigurator inner, DrainTimeoutCapturingConfigurator.Capture capture)
    : IInMemoryConfigurator
{
    /// <summary>Holds the last <see cref="IInMemoryConfigurator.DrainTimeout"/> value seen, if any.</summary>
    internal sealed class Capture
    {
        /// <summary>Gets or sets the last drain timeout value recorded, or <see langword="null"/> when none was configured.</summary>
        public TimeSpan? DrainTimeout { get; set; }
    }

    public void ConfigureTopology(Action<ITopologyConfigurator> configure) => inner.ConfigureTopology(configure);

    public void ReceiveEndpoint(string queueName, Action<IReceiveEndpointConfigurator> configure) =>
        inner.ReceiveEndpoint(queueName, configure);

    public void DefaultExchange(string exchangeName) => inner.DefaultExchange(exchangeName);

    public void GuaranteedRouting() => inner.GuaranteedRouting();

    public void AutoDeclareEndpointQueues() => inner.AutoDeclareEndpointQueues();

    public void QueueCapacity(int capacity) => inner.QueueCapacity(capacity);

    public void SendTimeout(TimeSpan timeout) => inner.SendTimeout(timeout);

    public void MaxMessageSize(int bytes) => inner.MaxMessageSize(bytes);

    public void MaxRedeliveries(int maxRedeliveries) => inner.MaxRedeliveries(maxRedeliveries);

    public void DrainTimeout(TimeSpan timeout)
    {
        capture.DrainTimeout = timeout;
        inner.DrainTimeout(timeout);
    }

    public void EnableDefer(TimeSpan? delay = null) => inner.EnableDefer(delay);

    public void MapRoutingKey<T>(string routingKey) where T : class => inner.MapRoutingKey<T>(routingKey);

    public void MapExchange<T>(string exchangeName) where T : class => inner.MapExchange<T>(exchangeName);

    public void Publish<T>(Action<IPublishConfigurator<T>> configure) where T : class => inner.Publish(configure);
}
