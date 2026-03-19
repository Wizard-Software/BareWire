using BareWire.Abstractions;
using BareWire.Samples.RawMessageInterop.Data;
using BareWire.Samples.RawMessageInterop.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.RawMessageInterop.Consumers;

/// <summary>
/// Processes strongly-typed <see cref="ExternalEvent"/> messages arriving on the <c>typed-events</c> queue.
/// Demonstrates automatic Content-Type routing — BareWire deserializes the payload before
/// passing it to this consumer, in contrast to <see cref="RawEventConsumer"/> which does so manually.
/// </summary>
internal sealed partial class TypedEventConsumer(
    InteropDbContext db,
    ILogger<TypedEventConsumer> logger) : IConsumer<ExternalEvent>
{
    public async Task ConsumeAsync(ConsumeContext<ExternalEvent> context)
    {
        ExternalEvent evt = context.Message;

        // Extract the correlation ID from the mapped header (mapped from X-Correlation-Id in Program.cs).
        context.Headers.TryGetValue("CorrelationId", out string? correlationId);

        LogReceived(logger, evt.EventType, evt.SourceSystem, correlationId ?? "(none)");

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            EventType = evt.EventType,
            Payload = evt.Payload,
            SourceSystem = evt.SourceSystem,
            CorrelationId = correlationId,
            ProcessedAt = DateTimeOffset.UtcNow,
            ConsumerType = "Typed",
        });

        await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        LogPersisted(logger, context.MessageId);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "TypedEventConsumer: received {EventType} from {SourceSystem} (correlationId={CorrelationId})")]
    private static partial void LogReceived(
        ILogger logger, string eventType, string sourceSystem, string correlationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "TypedEventConsumer: persisted message {MessageId} successfully")]
    private static partial void LogPersisted(ILogger logger, Guid messageId);
}
