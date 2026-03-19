using BareWire.Abstractions;
using BareWire.Samples.MultiConsumerPartitioning.Data;
using BareWire.Samples.MultiConsumerPartitioning.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.MultiConsumerPartitioning.Consumers;

/// <summary>
/// Consumes <see cref="ShipmentEvent"/> messages, logs the event, and persists a
/// <see cref="ProcessingLogEntry"/> to PostgreSQL.
/// </summary>
/// <remarks>
/// Resolved from DI per-message (transient lifetime). PartitionerMiddleware ensures that
/// all messages sharing the same CorrelationId are processed sequentially, regardless of
/// ConcurrentMessageLimit, enabling per-correlation ordering guarantees.
/// </remarks>
public sealed partial class ShipmentEventConsumer(
    ILogger<ShipmentEventConsumer> logger,
    PartitionDbContext dbContext) : IConsumer<ShipmentEvent>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<ShipmentEvent> context)
    {
        ShipmentEvent message = context.Message;
        int threadId = Environment.CurrentManagedThreadId;

        LogShipmentEventReceived(logger, message.ShipmentId, message.CorrelationId, threadId);

        dbContext.ProcessingLog.Add(new ProcessingLogEntry
        {
            CorrelationId = message.CorrelationId,
            ConsumerType = "ShipmentEvent",
            MessageType = nameof(ShipmentEvent),
            ProcessedAt = DateTime.UtcNow,
            ThreadId = threadId,
        });

        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ShipmentEvent received: ShipmentId={ShipmentId} CorrelationId={CorrelationId} ThreadId={ThreadId}")]
    private static partial void LogShipmentEventReceived(
        ILogger logger, string shipmentId, string correlationId, int threadId);
}
