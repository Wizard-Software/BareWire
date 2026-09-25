using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Topology;
using BareWire.Samples.InMemoryModularMonolith.Modules.Billing;
using BareWire.Samples.InMemoryModularMonolith.Modules.Ordering;
using BareWire.Samples.InMemoryModularMonolith.Modules.Shipping;

namespace BareWire.Samples.InMemoryModularMonolith.Messaging;

/// <summary>
/// The single place where the topology, the default exchange, and the three receive endpoints are
/// wired — shared verbatim by both transports. <see cref="TransportRegistration.AddModulithTransport"/>
/// passes each transport's own <c>ConfigureTopology</c> / <c>DefaultExchange</c> / <c>ReceiveEndpoint</c>
/// delegates through unchanged; nothing here is transport-specific.
/// </summary>
internal static class ModulithMessaging
{
    /// <summary>
    /// Declares topology, the default exchange, and the three receive endpoints shared by both
    /// transports.
    /// </summary>
    /// <param name="configureTopology">The active transport's <c>ConfigureTopology</c> method group.</param>
    /// <param name="defaultExchange">The active transport's <c>DefaultExchange</c> method group.</param>
    /// <param name="receiveEndpoint">The active transport's <c>ReceiveEndpoint</c> method group.</param>
    public static void Configure(
        Action<Action<ITopologyConfigurator>> configureTopology,
        Action<string> defaultExchange,
        Action<string, Action<IReceiveEndpointConfigurator>> receiveEndpoint)
    {
        configureTopology(ModulithTopology.ConfigureTopology);

        // POST /orders publishes OrderPlaced with no per-type mapping, so it resolves through
        // DefaultExchange to the fanout exchange bound to both module queues below.
        defaultExchange(ModulithTopology.OrderingExchange);

        receiveEndpoint(ModulithTopology.BillingOrderPlacedQueue, e =>
        {
            e.Consumer<OrderPlacedBillingConsumer, OrderPlaced>();
        });

        receiveEndpoint(ModulithTopology.ShippingOrderPlacedQueue, e =>
        {
            e.Consumer<OrderPlacedShippingConsumer, OrderPlaced>();
        });

        receiveEndpoint(ModulithTopology.ShippingPaymentCapturedQueue, e =>
        {
            e.Consumer<PaymentCapturedShippingConsumer, PaymentCaptured>();
        });
    }
}
