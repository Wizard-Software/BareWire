using BareWire.Abstractions;
using BareWire.Samples.MassTransitInterop.Messages;
using BareWire.Samples.MassTransitInterop.Services;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.MassTransitInterop.Consumers;

/// <summary>
/// Processes <see cref="OrderCreated"/> messages arriving from a MassTransit producer
/// on the <c>mt-orders-queue</c> queue (envelope format: <c>application/vnd.masstransit+json</c>).
/// Demonstrates that the same <see cref="IConsumer{T}"/> interface works regardless of the
/// envelope format — BareWire's <c>ContentTypeDeserializerRouter</c> unwraps the MassTransit
/// envelope transparently before this consumer is invoked.
/// </summary>
internal sealed partial class MassTransitOrderConsumer(
    ILogger<MassTransitOrderConsumer> logger,
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

        store.Add(new ProcessedOrder(order.OrderId, order.Amount, order.Currency, "MassTransit", DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MassTransitOrderConsumer: received OrderCreated from MassTransit envelope " +
                  "(orderId={OrderId}, amount={Amount}, currency={Currency}, correlationId={CorrelationId})")]
    private static partial void LogReceived(
        ILogger logger, string orderId, decimal amount, string currency, string correlationId);
}
