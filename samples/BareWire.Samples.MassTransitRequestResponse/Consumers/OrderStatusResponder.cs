using BareWire.Samples.MassTransitRequestResponse.Messages;
using MassTransit;

namespace BareWire.Samples.MassTransitRequestResponse.Consumers;

/// <summary>
/// MassTransit consumer that answers BareWire order-status requests.
///
/// Receives <see cref="CheckOrderStatus"/> from a RabbitMQ queue and replies via
/// <c>context.RespondAsync</c>. MassTransit detects that the <c>responseAddress</c> field
/// in the BareWire envelope ends with <c>amq.rabbitmq.reply-to</c> and routes the reply
/// through the AMQP direct reply-to mechanism (the AMQP message's <c>ReplyTo</c> field).
/// BareWire receives the reply on its temporary reply queue.
/// </summary>
public sealed partial class OrderStatusResponder : IConsumer<CheckOrderStatus>
{
    private readonly ILogger<OrderStatusResponder> _logger;

    public OrderStatusResponder(ILogger<OrderStatusResponder> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CheckOrderStatus> context)
    {
        string orderId = context.Message.OrderId;

        // Materialize the nullable values before passing to the logger to avoid
        // CA1873 (expensive nullable conversion inside the log call).
        string requestId = context.RequestId?.ToString() ?? "null";
        string responseAddress = context.ResponseAddress?.ToString() ?? "null";

        Log.ReceivedRequest(_logger, orderId, requestId, responseAddress);

        // Simulated business logic — a real application would query a database here.
        string status = orderId.StartsWith("ERR", StringComparison.OrdinalIgnoreCase)
            ? "ProcessingError"
            : "Confirmed";

        await context.RespondAsync(new OrderStatus(
            OrderId: orderId,
            Status: status,
            ProcessedBy: "MassTransit/OrderStatusResponder"));
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "MassTransit: received order-status request {OrderId}. requestId={RequestId}, responseAddress={ResponseAddress}")]
        internal static partial void ReceivedRequest(ILogger logger, string orderId, string requestId, string responseAddress);
    }
}
