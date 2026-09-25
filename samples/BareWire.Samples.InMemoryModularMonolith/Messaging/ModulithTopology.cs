using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Samples.InMemoryModularMonolith.Modules.Billing;

namespace BareWire.Samples.InMemoryModularMonolith.Messaging;

/// <summary>
/// Declares the manual topology shared by both transports: two exchanges (one fanout, one topic) and
/// the three queues bound to them. <see cref="ConfigureTopology"/> is passed unchanged to
/// <c>ConfigureTopology</c> on either <c>IInMemoryConfigurator</c> or <c>IRabbitMqConfigurator</c> —
/// the topology itself never differs between transports.
/// </summary>
internal static class ModulithTopology
{
    /// <summary>Fanout exchange fed by <c>POST /orders</c>. Bound to both module queues below.</summary>
    public const string OrderingExchange = "modulith.ordering";

    /// <summary>Topic exchange fed by Billing's <c>PaymentCaptured</c> publish.</summary>
    public const string BillingExchange = "modulith.billing";

    /// <summary>Routing key Billing publishes <c>PaymentCaptured</c> with.</summary>
    public const string PaymentCapturedRoutingKey = "billing.payment.captured";

    /// <summary>Binding pattern Shipping's payment-captured queue matches against.</summary>
    public const string PaymentBindingPattern = "billing.payment.*";

    /// <summary>Queue consumed by <see cref="Modules.Billing.OrderPlacedBillingConsumer"/>.</summary>
    public const string BillingOrderPlacedQueue = "billing.order-placed";

    /// <summary>Queue consumed by <see cref="Modules.Shipping.OrderPlacedShippingConsumer"/>.</summary>
    public const string ShippingOrderPlacedQueue = "shipping.order-placed";

    /// <summary>Queue consumed by <see cref="Modules.Shipping.PaymentCapturedShippingConsumer"/>.</summary>
    public const string ShippingPaymentCapturedQueue = "shipping.payment-captured";

    /// <summary>
    /// Declares the exchanges, queues, and bindings for the modular monolith. Called once per
    /// transport via <c>ConfigureTopology</c> — identical for both.
    /// </summary>
    /// <param name="topology">The topology configurator supplied by the active transport.</param>
    public static void ConfigureTopology(ITopologyConfigurator topology)
    {
        // OrderingExchange is a plain (non-generic) declaration: OrderPlaced uses the transport's
        // DefaultExchange rather than a per-type mapping (see TransportRegistration).
        topology.DeclareExchange(OrderingExchange, ExchangeType.Fanout, durable: true, autoDelete: false);

        // The generic overload both declares BillingExchange AND registers the PaymentCaptured →
        // (exchange, routing key) mapping used by PublishAsync<PaymentCaptured> in one call — see
        // ITopologyConfigurator.DeclareExchange<T> "declare + map" remarks.
        topology.DeclareExchange<PaymentCaptured>(
            BillingExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            routingKey: PaymentCapturedRoutingKey);

        topology.DeclareQueue(BillingOrderPlacedQueue, durable: true);
        topology.DeclareQueue(ShippingOrderPlacedQueue, durable: true);
        topology.DeclareQueue(ShippingPaymentCapturedQueue, durable: true);

        // Fanout ignores the routing key at match time; "#" documents intent (matches the convention
        // used by the other fanout-based samples in this repository).
        topology.BindExchangeToQueue(OrderingExchange, BillingOrderPlacedQueue, routingKey: "#");
        topology.BindExchangeToQueue(OrderingExchange, ShippingOrderPlacedQueue, routingKey: "#");

        topology.BindExchangeToQueue(BillingExchange, ShippingPaymentCapturedQueue, routingKey: PaymentBindingPattern);
    }
}
