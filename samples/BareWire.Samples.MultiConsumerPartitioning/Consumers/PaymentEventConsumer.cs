using BareWire.Abstractions;
using BareWire.Samples.MultiConsumerPartitioning.Data;
using BareWire.Samples.MultiConsumerPartitioning.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.MultiConsumerPartitioning.Consumers;

/// <summary>
/// Consumes <see cref="PaymentEvent"/> messages, logs the event, and persists a
/// <see cref="ProcessingLogEntry"/> to PostgreSQL.
/// </summary>
/// <remarks>
/// Resolved from DI per-message (transient lifetime). PartitionerMiddleware ensures that
/// all messages sharing the same CorrelationId are processed sequentially, regardless of
/// ConcurrentMessageLimit, enabling per-correlation ordering guarantees.
/// </remarks>
public sealed partial class PaymentEventConsumer(
    ILogger<PaymentEventConsumer> logger,
    PartitionDbContext dbContext) : IConsumer<PaymentEvent>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<PaymentEvent> context)
    {
        PaymentEvent message = context.Message;
        int threadId = Environment.CurrentManagedThreadId;

        LogPaymentEventReceived(logger, message.PaymentId, message.CorrelationId, threadId);

        dbContext.ProcessingLog.Add(new ProcessingLogEntry
        {
            CorrelationId = message.CorrelationId,
            ConsumerType = "PaymentEvent",
            MessageType = nameof(PaymentEvent),
            ProcessedAt = DateTime.UtcNow,
            ThreadId = threadId,
        });

        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "PaymentEvent received: PaymentId={PaymentId} CorrelationId={CorrelationId} ThreadId={ThreadId}")]
    private static partial void LogPaymentEventReceived(
        ILogger logger, string paymentId, string correlationId, int threadId);
}
