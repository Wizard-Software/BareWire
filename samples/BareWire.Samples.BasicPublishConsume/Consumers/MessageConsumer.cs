using BareWire.Abstractions;
using BareWire.Samples.BasicPublishConsume.Data;
using BareWire.Samples.BasicPublishConsume.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.BasicPublishConsume.Consumers;

/// <summary>
/// Consumes <see cref="MessageSent"/> events, logs the content, and persists a
/// <see cref="ReceivedMessage"/> record to PostgreSQL via EF Core.
/// </summary>
/// <remarks>
/// Resolved from DI per-message (transient lifetime). Keep this class stateless —
/// <see cref="SampleDbContext"/> is scoped and injected per-message dispatch.
/// </remarks>
public sealed partial class MessageConsumer(
    ILogger<MessageConsumer> logger,
    SampleDbContext dbContext) : IConsumer<MessageSent>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<MessageSent> context)
    {
        MessageSent message = context.Message;

        LogMessageReceived(logger, message.Content, message.SentAt);

        ReceivedMessage entity = new()
        {
            Content = message.Content,
            ReceivedAt = DateTime.UtcNow,
        };

        dbContext.ReceivedMessages.Add(entity);
        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        LogMessagePersisted(logger, entity.Id);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Received message: '{Content}' sent at {SentAt:O}")]
    private static partial void LogMessageReceived(
        ILogger logger, string content, DateTime sentAt);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Persisted received message with Id={MessageId}")]
    private static partial void LogMessagePersisted(
        ILogger logger, Guid messageId);
}
