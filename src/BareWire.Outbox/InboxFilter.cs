using Microsoft.Extensions.Logging;

namespace BareWire.Outbox;

internal sealed partial class InboxFilter
{
    private readonly IInboxStore _store;
    private readonly OutboxOptions _options;
    private readonly ILogger<InboxFilter> _logger;
    private readonly InboxDiagnostics? _diagnostics;

    internal InboxFilter(
        IInboxStore store,
        OutboxOptions options,
        ILogger<InboxFilter> logger,
        InboxDiagnostics? diagnostics = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diagnostics = diagnostics;
    }

    internal async ValueTask<bool> TryLockAsync(
        Guid messageId,
        string consumerType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumerType);

        bool acquired = await _store
            .TryLockAsync(messageId, consumerType, _options.InboxLockTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            InboxFilterLogMessages.DuplicateMessageSkipped(_logger, messageId);
            _diagnostics?.DuplicateDetected(consumerType);
        }

        return acquired;
    }

    internal async ValueTask MarkProcessedAsync(
        Guid messageId,
        string consumerType,
        CancellationToken cancellationToken = default)
    {
        await _store.MarkProcessedAsync(messageId, consumerType, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal static partial class InboxFilterLogMessages
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Duplicate message {MessageId} skipped — lock already held")]
    internal static partial void DuplicateMessageSkipped(
        ILogger logger,
        Guid messageId);
}
