using BareWire.Abstractions;
using BareWire.Samples.RabbitMQ.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.RabbitMQ.Consumers;

/// <summary>
/// Processes <see cref="OrderCreated"/> events and sends an <see cref="OrderProcessed"/>
/// confirmation to the dedicated <c>order-processed.events</c> exchange via
/// <see cref="IConsumeContext.GetSendEndpoint"/> once the order has been handled.
/// </summary>
/// <remarks>
/// Sending to a separate exchange (rather than publishing to the default <c>rmq-sample.order.events</c>
/// exchange) prevents an infinite message loop: <c>order-processed.events</c> is not bound
/// to the <c>orders</c> queue, so <see cref="OrderProcessed"/> messages never re-enter this consumer.
/// <para>
/// Resolved from DI per-message (scoped lifetime). Keep this class stateless — any state
/// that needs to outlive a single message dispatch must live in a scoped or singleton service.
/// </para>
/// </remarks>
public sealed partial class OrderConsumer(ILogger<OrderConsumer> logger) : IConsumer<OrderCreated>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<OrderCreated> context)
    {
        OrderCreated order = context.Message;

        LogProcessingOrder(logger, order.OrderId, order.Amount, order.Currency);

        // Simulate lightweight business logic (e.g. validation, enrichment).
        // In production code, inject domain services and repositories here.
        await Task.Delay(millisecondsDelay: 0, context.CancellationToken).ConfigureAwait(false);

        // Send the outcome to a dedicated exchange to avoid a publish cycle.
        // "order-processed.events" is not bound to the "orders" queue, so OrderProcessed
        // messages never loop back through this consumer.
        ISendEndpoint endpoint = await context.GetSendEndpoint(
            new Uri("exchange:order-processed.events"),
            context.CancellationToken).ConfigureAwait(false);

        await endpoint.SendAsync(
            new OrderProcessed(order.OrderId, "Accepted"),
            context.CancellationToken).ConfigureAwait(false);

        LogOrderAccepted(logger, order.OrderId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing order {OrderId} for {Amount} {Currency}")]
    private static partial void LogProcessingOrder(
        ILogger logger, string orderId, decimal amount, string currency);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} accepted and OrderProcessed published")]
    private static partial void LogOrderAccepted(ILogger logger, string orderId);
}
