using BareWire.Abstractions.Pipeline;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox;

internal sealed partial class InMemoryOutboxMiddleware : IMessageMiddleware
{
    private static readonly AsyncLocal<OutboxBuffer?> _current = new();

    private readonly IOutboxStore _outboxStore;
    private readonly ILogger<InMemoryOutboxMiddleware> _logger;

    internal static OutboxBuffer? Current => _current.Value;

    private readonly int _maxBufferedMessages;
    private readonly long _maxBufferedBytes;

    internal InMemoryOutboxMiddleware(IOutboxStore outboxStore, ILogger<InMemoryOutboxMiddleware> logger)
        : this(outboxStore, logger, OutboxOptions.Default)
    {
    }

    internal InMemoryOutboxMiddleware(
        IOutboxStore outboxStore,
        ILogger<InMemoryOutboxMiddleware> logger,
        OutboxOptions options)
    {
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        _maxBufferedMessages = options.MaxBufferedMessagesPerConsume;
        _maxBufferedBytes = options.MaxBufferedBytesPerConsume;
    }

    public async Task InvokeAsync(MessageContext context, NextMiddleware nextMiddleware)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nextMiddleware);

        var buffer = new OutboxBuffer(_maxBufferedMessages, _maxBufferedBytes);
        _current.Value = buffer;

        // In-process retries (RetryMiddleware sits inside this middleware) re-run the handler on the
        // same buffer; discard what the failed attempt published so only the successful attempt's
        // messages reach the outbox.
        Action retryCallback = buffer.Clear;
        context.Items[WellKnownItemKeys.RetryAttemptStarting] = retryCallback;

        try
        {
            await nextMiddleware(context).ConfigureAwait(false);

            // No message may be appended once the snapshot is taken for persistence.
            buffer.Seal();

            if (!buffer.IsEmpty)
            {
                var messages = buffer.GetMessages();
                InMemoryOutboxMiddlewareLogMessages.FlushingBuffer(_logger, context.MessageId, messages.Count);
                await _outboxStore.SaveMessagesAsync(messages, context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            int discardCount = buffer.GetMessages().Count;
            InMemoryOutboxMiddlewareLogMessages.DiscardingBuffer(_logger, context.MessageId, discardCount);
            buffer.Clear();
            throw;
        }
        finally
        {
            // Also stops background work from a failed attempt from appending after the buffer is discarded.
            buffer.Seal();
            _current.Value = null;

            if (!context.Items.TryGetValue(WellKnownItemKeys.RetryAttemptStarting, out object? slot)
                || !ReferenceEquals(slot, retryCallback))
            {
                InMemoryOutboxMiddlewareLogMessages.RetryCallbackOverwritten(_logger, context.MessageId);
            }

            context.Items.Remove(WellKnownItemKeys.RetryAttemptStarting);
        }
    }
}

internal static partial class InMemoryOutboxMiddlewareLogMessages
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Flushing outbox buffer for message {MessageId}: {MessageCount} message(s) to store")]
    internal static partial void FlushingBuffer(
        ILogger logger,
        Guid messageId,
        int messageCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Discarding outbox buffer for message {MessageId}: {MessageCount} buffered message(s) lost due to handler exception")]
    internal static partial void DiscardingBuffer(
        ILogger logger,
        Guid messageId,
        int messageCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The retry-attempt callback registered by the in-memory outbox for message {MessageId} was " +
                  "overwritten or removed by other middleware; messages from failed retry attempts may be duplicated")]
    internal static partial void RetryCallbackOverwritten(
        ILogger logger,
        Guid messageId);
}
