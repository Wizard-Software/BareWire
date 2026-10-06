using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// Redirects messages published while a <see cref="TransactionalOutboxMiddleware"/> consume operation is
/// active into that operation's <see cref="OutboxBuffer"/>, so they are written to the outbox in the same
/// transaction as the handler's business changes and dispatched only after commit.
/// </summary>
internal sealed partial class TransactionalOutboxInterceptor : IOutboundMessageInterceptor
{
    private readonly ILogger<TransactionalOutboxInterceptor> _logger;

    public TransactionalOutboxInterceptor(ILogger<TransactionalOutboxInterceptor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool IsCapturing => TransactionalOutboxMiddleware.Current is not null;

    public bool TryIntercept(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboxBuffer? buffer = TransactionalOutboxMiddleware.Current;
        if (buffer is null)
            return false;

        if (buffer.TryAdd(message))
            return true;

        // The consume operation already persisted (or discarded) its buffer: this message comes from work
        // that outlives the handler. Let the bus send it directly (non-transactionally) rather than lose it.
        message.Headers.TryGetValue("message-id", out string? messageId);
        message.Headers.TryGetValue("BW-MessageType", out string? messageType);
        LateMessageBypassesOutbox(_logger, messageId ?? string.Empty, messageType ?? string.Empty);
        return false;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Message {MessageId} ({MessageType}) was published after its consume operation finished; " +
                  "it is sent directly to the transport and is NOT covered by the transactional outbox")]
    private static partial void LateMessageBypassesOutbox(ILogger logger, string messageId, string messageType);
}
