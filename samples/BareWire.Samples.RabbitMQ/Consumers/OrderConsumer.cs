using BareWire.Abstractions;
using BareWire.Samples.RabbitMQ.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.RabbitMQ.Consumers;

/// <summary>
/// Processes <see cref="OrderCreated"/> events and publishes an <see cref="OrderProcessed"/>
/// confirmation back to the bus once the order has been handled.
/// </summary>
/// <remarks>
/// Resolved from DI per-message (scoped lifetime). Keep this class stateless — any state
/// that needs to outlive a single message dispatch must live in a scoped or singleton service.
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

        // Publish the outcome — the bus routes this to all subscribers of OrderProcessed,
        // including the OrderSagaStateMachine running on the "order-saga" endpoint.
        await context.PublishAsync(
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
