using BareWire.Samples.MassTransitRequestResponse.Messages;
using MassTransit;

namespace BareWire.Samples.MassTransitRequestResponse.Consumers;

/// <summary>
/// MassTransit consumer odpowiadający na zapytania BareWire o status zamówienia.
///
/// Odbiera <see cref="CheckOrderStatus"/> z kolejki RabbitMQ i odpowiada przez
/// <c>context.RespondAsync</c>. MassTransit wykrywa, że pole <c>responseAddress</c>
/// w kopercie BareWire kończy się na <c>amq.rabbitmq.reply-to</c> i kieruje odpowiedź
/// bezpośrednio przez mechanizm AMQP Direct Reply-To (pole <c>ReplyTo</c> na wiadomości AMQP).
/// BareWire odbiera odpowiedź na swojej tymczasowej kolejce odpowiedzi.
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

        // Symulacja logiki biznesowej — w rzeczywistej aplikacji tu byłoby zapytanie do bazy danych.
        string status = orderId.StartsWith("ERR", StringComparison.OrdinalIgnoreCase)
            ? "Błąd przetwarzania"
            : "Potwierdzono";

        await context.RespondAsync(new OrderStatus(
            OrderId: orderId,
            Status: status,
            ProcessedBy: "MassTransit/OrderStatusResponder"));
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "MassTransit: odebrano zapytanie o status zamówienia {OrderId}. requestId={RequestId}, responseAddress={ResponseAddress}")]
        internal static partial void ReceivedRequest(ILogger logger, string orderId, string requestId, string responseAddress);
    }
}
