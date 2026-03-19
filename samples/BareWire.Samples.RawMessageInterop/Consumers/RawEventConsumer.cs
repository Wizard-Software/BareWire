using BareWire.Abstractions;
using BareWire.Samples.RawMessageInterop.Data;
using BareWire.Samples.RawMessageInterop.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.RawMessageInterop.Consumers;

/// <summary>
/// Processes raw (undeserialized) messages arriving from a legacy external system.
/// Demonstrates manual deserialization via <see cref="RawConsumeContext.TryDeserialize{T}"/>,
/// custom header extraction, and PostgreSQL persistence.
/// </summary>
/// <remarks>
/// This consumer is intentionally registered on the <c>raw-events</c> queue, which receives
/// messages published directly by <c>LegacyPublisher</c> using bare <c>RabbitMQ.Client</c>
/// without a BareWire envelope. The <c>ConfigureHeaderMapping</c> call in <c>Program.cs</c>
/// maps <c>X-Correlation-Id</c>, <c>X-Message-Type</c>, and <c>X-Source-System</c> to the
/// BareWire canonical header names before this consumer sees them.
/// </remarks>
internal sealed partial class RawEventConsumer(
    InteropDbContext db,
    ILogger<RawEventConsumer> logger) : IRawConsumer
{
    public async Task ConsumeAsync(RawConsumeContext context)
    {
        // Attempt to deserialize raw JSON body into ExternalEvent.
        ExternalEvent? externalEvent = context.TryDeserialize<ExternalEvent>();

        if (externalEvent is null)
        {
            LogDeserializationFailed(logger, context.MessageId);

            // Do not throw — move on to avoid infinite retry of a permanently malformed message.
            return;
        }

        // Extract the correlation ID from the mapped header (mapped from X-Correlation-Id in Program.cs).
        context.Headers.TryGetValue("CorrelationId", out string? correlationId);

        LogReceived(logger, externalEvent.EventType, externalEvent.SourceSystem, correlationId ?? "(none)");

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            EventType = externalEvent.EventType,
            Payload = externalEvent.Payload,
            SourceSystem = externalEvent.SourceSystem,
            CorrelationId = correlationId,
            ProcessedAt = DateTimeOffset.UtcNow,
            ConsumerType = "Raw",
        });

        await db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        LogPersisted(logger, context.MessageId);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "RawEventConsumer: failed to deserialize message {MessageId} — body is empty or malformed")]
    private static partial void LogDeserializationFailed(ILogger logger, Guid messageId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "RawEventConsumer: received {EventType} from {SourceSystem} (correlationId={CorrelationId})")]
    private static partial void LogReceived(
        ILogger logger, string eventType, string sourceSystem, string correlationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "RawEventConsumer: persisted message {MessageId} successfully")]
    private static partial void LogPersisted(ILogger logger, Guid messageId);
}
