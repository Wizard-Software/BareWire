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

    /// <summary>Gets the diagnostics duplicates are recorded on, or <see langword="null"/> when metrics are not wired.</summary>
    internal InboxDiagnostics? Diagnostics => _diagnostics;

    internal ValueTask<bool> TryLockAsync(
        Guid messageId,
        string consumerType,
        CancellationToken cancellationToken = default)
        => TryLockAsync(messageId, consumerType, metricConsumerTag: consumerType, cancellationToken);

    /// <summary>
    /// Tries to acquire the inbox lock for <paramref name="messageId"/> and <paramref name="consumerType"/>,
    /// recording a detected duplicate under <paramref name="metricConsumerTag"/> — a bounded identifier
    /// chosen by the caller, so a producer-controlled value used for deduplication never becomes a
    /// metric tag.
    /// </summary>
    internal async ValueTask<bool> TryLockAsync(
        Guid messageId,
        string consumerType,
        string metricConsumerTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumerType);
        ArgumentNullException.ThrowIfNull(metricConsumerTag);

        bool acquired = await _store
            .TryLockAsync(messageId, consumerType, _options.InboxLockTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            InboxFilterLogMessages.DuplicateMessageSkipped(_logger, messageId);
            _diagnostics?.DuplicateDetected(metricConsumerTag);
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
