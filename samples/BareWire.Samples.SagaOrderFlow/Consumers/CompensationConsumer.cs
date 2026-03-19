using BareWire.Abstractions;
using BareWire.Samples.SagaOrderFlow.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.SagaOrderFlow.Consumers;

/// <summary>
/// Consumes <see cref="CompensationCompleted"/> events and logs the outcome.
/// In a real system this would trigger post-compensation notifications (email, audit log, etc.).
/// </summary>
public sealed partial class CompensationConsumer(ILogger<CompensationConsumer> logger)
    : IConsumer<CompensationCompleted>
{
    /// <inheritdoc />
    public Task ConsumeAsync(ConsumeContext<CompensationCompleted> context)
    {
        LogCompensationCompleted(context.Message.OrderId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Compensation completed for order {OrderId} — order has been cancelled and all activities rolled back")]
    private partial void LogCompensationCompleted(string orderId);
}
