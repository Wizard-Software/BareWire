using BareWire.Abstractions;
using BareWire.Samples.MassTransitInterop.Messages;
using BareWire.Samples.MassTransitInterop.Services;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.MassTransitInterop.Consumers;

/// <summary>
/// Processes <see cref="OrderCreated"/> messages arriving from BareWire's <c>IBus.PublishAsync</c>
/// on the <c>barewire-orders-queue</c> queue (raw JSON: <c>application/json</c>).
/// Demonstrates ADR-001 raw-first: no envelope wrapper, plain JSON deserialized directly
/// into the message record.
/// </summary>
internal sealed partial class BareWireOrderConsumer(
    ILogger<BareWireOrderConsumer> logger,
    ProcessedOrderStore store) : IConsumer<OrderCreated>
{
    public Task ConsumeAsync(ConsumeContext<OrderCreated> context)
    {
        OrderCreated order = context.Message;

        context.Headers.TryGetValue("correlation-id", out string? correlationId);

        LogReceived(
            logger,
            order.OrderId,
            order.Amount,
            order.Currency,
            correlationId ?? "(none)");

        store.Add(new ProcessedOrder(order.OrderId, order.Amount, order.Currency, "BareWire", DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "BareWireOrderConsumer: received OrderCreated from raw JSON " +
                  "(orderId={OrderId}, amount={Amount}, currency={Currency}, correlationId={CorrelationId})")]
    private static partial void LogReceived(
        ILogger logger, string orderId, decimal amount, string currency, string correlationId);
}
