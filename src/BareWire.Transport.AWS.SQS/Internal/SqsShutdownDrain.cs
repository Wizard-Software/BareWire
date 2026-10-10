using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Amazon.SQS;
using Amazon.SQS.Model;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AWS.SQS.Internal;

/// <summary>
/// Consumer shutdown helpers: reading the buffer only while the consumer token is live, and handing
/// every message the caller never received back to SQS (<c>VisibilityTimeout = 0</c>).
/// </summary>
/// <remarks>
/// All release paths share one <see cref="SqsShutdownBudget"/>; once it is spent, or after the first
/// failed request, the remaining messages are only disposed and evicted (they reappear after their
/// visibility timeout). Nothing here throws, and nothing logs receipt handles or exception messages.
/// </remarks>
internal static partial class SqsShutdownDrain
{
    /// <summary>The SQS limit of entries per <c>ChangeMessageVisibilityBatch</c> request.</summary>
    internal const int BatchSize = 10;

    /// <summary>
    /// Yields buffered messages, checking the token before every read so a cancelled consumer never takes
    /// another message out of the buffer (the BCL <c>ReadAllAsync</c> only checks it while waiting).
    /// </summary>
    internal static async IAsyncEnumerable<InboundMessage> ReadUntilCancelledAsync(
        ChannelReader<InboundMessage> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (!cancellationToken.IsCancellationRequested && reader.TryRead(out InboundMessage? message))
            {
                yield return message;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Empties the buffer: disposes every message, evicts its registry entry and (within the budget)
    /// makes it visible again.
    /// </summary>
    internal static async Task DrainAndReleaseAsync(
        ChannelReader<InboundMessage> reader,
        SqsInFlightRegistry registry,
        IAmazonSQS client,
        string queueUrl,
        string endpointName,
        SqsShutdownBudget budget,
        ILogger logger)
    {
        var releaser = new BatchReleaser(client, queueUrl, budget);

        while (reader.TryRead(out InboundMessage? message))
        {
            string messageId = message.MessageId;
            (string ReceiptHandle, string QueueUrl)? entry = registry.TryEvict(message.DeliveryTag);
            message.Dispose();

            if (entry is not null)
            {
                await releaser.AddAsync(entry.Value.ReceiptHandle, messageId).ConfigureAwait(false);
            }
        }

        await releaser.FlushAsync().ConfigureAwait(false);
        releaser.Report(logger, endpointName);
    }

    /// <summary>
    /// Hands back messages that were received but never registered or buffered (the consumer was already
    /// stopping when the response arrived).
    /// </summary>
    internal static async Task ReleaseUnprocessedAsync(
        IAmazonSQS client,
        string queueUrl,
        string endpointName,
        IReadOnlyList<Message> messages,
        int startIndex,
        SqsShutdownBudget budget,
        ILogger logger)
    {
        var releaser = new BatchReleaser(client, queueUrl, budget);

        for (int i = startIndex; i < messages.Count; i++)
        {
            await releaser.AddAsync(messages[i].ReceiptHandle, messages[i].MessageId).ConfigureAwait(false);
        }

        await releaser.FlushAsync().ConfigureAwait(false);
        releaser.Report(logger, endpointName);
    }

    private sealed class BatchReleaser(IAmazonSQS client, string queueUrl, SqsShutdownBudget budget)
    {
        private readonly List<string> _handles = new(BatchSize);
        private readonly List<string> _messageIds = new(BatchSize);
        private bool _disposeOnly;

        private int _skipped;
        private int _failedEntries;
        private bool _budgetExhausted;
        private SqsErrorInfo? _error;
        private string? _firstFailedMessageId;
        private string? _firstFailedEntryCode;

        internal async Task AddAsync(string receiptHandle, string messageId)
        {
            if (_disposeOnly)
            {
                _skipped++;
                return;
            }

            _handles.Add(receiptHandle);
            _messageIds.Add(messageId);

            if (_handles.Count == BatchSize)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }

        internal async Task FlushAsync()
        {
            if (_handles.Count == 0)
            {
                return;
            }

            int count = _handles.Count;
            CancellationToken token = budget.Token;

            try
            {
                if (token.IsCancellationRequested)
                {
                    _budgetExhausted = true;
                    StopReleasing(count);
                    return;
                }

                var entries = new List<ChangeMessageVisibilityBatchRequestEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    entries.Add(new ChangeMessageVisibilityBatchRequestEntry
                    {
                        Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ReceiptHandle = _handles[i],
                        VisibilityTimeout = 0,
                    });
                }

                ChangeMessageVisibilityBatchResponse? response = await client
                    .ChangeMessageVisibilityBatchAsync(
                        new ChangeMessageVisibilityBatchRequest { QueueUrl = queueUrl, Entries = entries },
                        token)
                    .ConfigureAwait(false);

                if (response?.Failed is { Count: > 0 } failed)
                {
                    // Failed entries are not retried: the message reappears after its visibility timeout.
                    _failedEntries += failed.Count;
                    _firstFailedEntryCode ??= failed[0].Code;
                    if (_firstFailedMessageId is null
                        && int.TryParse(failed[0].Id, out int index)
                        && index >= 0 && index < count)
                    {
                        _firstFailedMessageId = _messageIds[index];
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _budgetExhausted = true;
                _firstFailedMessageId ??= _messageIds[0];
                StopReleasing(count);
            }
            catch (Exception ex)
            {
                // Handled by design: shutdown must never throw. Record only loggable facts and fall back
                // to dispose-only for the rest of the drain.
                _error ??= SqsErrorInfo.From(ex);
                _firstFailedMessageId ??= _messageIds[0];
                StopReleasing(count);
            }
            finally
            {
                _handles.Clear();
                _messageIds.Clear();
            }
        }

        internal void Report(ILogger logger, string endpointName)
        {
            if (_budgetExhausted)
            {
                LogBudgetExhausted(logger, endpointName, _skipped, _firstFailedMessageId ?? string.Empty);
            }

            if (_error is { } error)
            {
                LogReleaseFailed(
                    logger, endpointName, _skipped, _firstFailedMessageId ?? string.Empty,
                    error.ExceptionType, error.ErrorCode, error.StatusCode);
            }

            if (_failedEntries > 0)
            {
                LogReleasePartiallyFailed(
                    logger, endpointName, _failedEntries, _firstFailedMessageId ?? string.Empty,
                    _firstFailedEntryCode ?? "none");
            }
        }

        private void StopReleasing(int pendingCount)
        {
            _disposeOnly = true;
            _skipped += pendingCount;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS shutdown release budget exhausted for queue '{QueueName}': {SkippedCount} message(s) were " +
                  "disposed without being made visible again (first MessageId={MessageId}). " +
                  "They reappear after their visibility timeout.")]
    private static partial void LogBudgetExhausted(
        ILogger logger, string queueName, int skippedCount, string messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS shutdown release failed for queue '{QueueName}': {SkippedCount} message(s) were disposed " +
                  "without being made visible again (first MessageId={MessageId}, {ExceptionType}, " +
                  "ErrorCode={ErrorCode}, StatusCode={StatusCode}). They reappear after their visibility timeout.")]
    private static partial void LogReleaseFailed(
        ILogger logger, string queueName, int skippedCount, string messageId,
        string exceptionType, string errorCode, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS shutdown release partially failed for queue '{QueueName}': {FailedCount} entr(ies) were " +
                  "rejected (first MessageId={MessageId}, ErrorCode={ErrorCode}). " +
                  "They reappear after their visibility timeout.")]
    private static partial void LogReleasePartiallyFailed(
        ILogger logger, string queueName, int failedCount, string messageId, string errorCode);
}
