using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BareWire.Abstractions.Transport;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Google.PubSub.Internal;

/// <summary>
/// Consumer shutdown helpers: reading the buffer only while the consumer token is live, and handing
/// every message the caller never received back to Pub/Sub (<c>ModifyAckDeadline(0)</c>).
/// </summary>
/// <remarks>
/// All release paths share one <see cref="PubSubShutdownBudget"/>; once it is spent, or after the first
/// failed request, the remaining messages are only disposed and evicted (they are redelivered after their
/// ack deadline). Nothing here throws, and nothing logs ack ids or exception messages.
/// </remarks>
internal static partial class PubSubShutdownDrain
{
    /// <summary>
    /// Maximum ack ids per <c>ModifyAckDeadline</c> request; keeps the request well below the 512 KB limit.
    /// </summary>
    internal const int ChunkSize = 1000;

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
    /// makes it deliverable again.
    /// </summary>
    internal static async Task DrainAndReleaseAsync(
        ChannelReader<InboundMessage> reader,
        PubSubInFlightRegistry registry,
        SubscriberServiceApiClient subscriber,
        string subscriptionName,
        PubSubShutdownBudget budget,
        ILogger logger)
    {
        var releaser = new ChunkReleaser(subscriber, subscriptionName, budget);

        while (reader.TryRead(out InboundMessage? message))
        {
            string messageId = message.MessageId;
            (string AckId, string SubscriptionName)? entry = registry.TryEvict(message.DeliveryTag);
            message.Dispose();

            if (entry is not null)
            {
                await releaser.AddAsync(entry.Value.AckId, messageId).ConfigureAwait(false);
            }
        }

        await releaser.FlushAsync().ConfigureAwait(false);
        releaser.Report(logger, subscriptionName);
    }

    /// <summary>
    /// Hands back messages that were received but never registered or buffered (the consumer was already
    /// stopping when the response arrived).
    /// </summary>
    internal static async Task ReleaseUnprocessedAsync(
        SubscriberServiceApiClient subscriber,
        string subscriptionName,
        IReadOnlyList<ReceivedMessage> messages,
        int startIndex,
        PubSubShutdownBudget budget,
        ILogger logger)
    {
        var releaser = new ChunkReleaser(subscriber, subscriptionName, budget);

        for (int i = startIndex; i < messages.Count; i++)
        {
            await releaser.AddAsync(messages[i].AckId, messages[i].Message.MessageId).ConfigureAwait(false);
        }

        await releaser.FlushAsync().ConfigureAwait(false);
        releaser.Report(logger, subscriptionName);
    }

    private sealed class ChunkReleaser(
        SubscriberServiceApiClient subscriber, string subscriptionName, PubSubShutdownBudget budget)
    {
        private readonly List<string> _ackIds = [];
        private string? _chunkFirstMessageId;
        private bool _disposeOnly;

        private int _skipped;
        private bool _budgetExhausted;
        private PubSubErrorInfo? _error;
        private string? _firstFailedMessageId;

        internal async Task AddAsync(string ackId, string messageId)
        {
            if (_disposeOnly)
            {
                _skipped++;
                return;
            }

            _ackIds.Add(ackId);
            _chunkFirstMessageId ??= messageId;

            if (_ackIds.Count == ChunkSize)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }

        internal async Task FlushAsync()
        {
            if (_ackIds.Count == 0)
            {
                return;
            }

            int count = _ackIds.Count;
            CancellationToken token = budget.Token;

            try
            {
                if (token.IsCancellationRequested)
                {
                    _budgetExhausted = true;
                    _firstFailedMessageId ??= _chunkFirstMessageId;
                    StopReleasing(count);
                    return;
                }

                await subscriber.ModifyAckDeadlineAsync(
                    subscriptionName,
                    _ackIds,
                    ackDeadlineSeconds: 0,
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _budgetExhausted = true;
                _firstFailedMessageId ??= _chunkFirstMessageId;
                StopReleasing(count);
            }
            catch (Exception ex)
            {
                // Handled by design: shutdown must never throw. Record only loggable facts and fall back
                // to dispose-only for the rest of the drain.
                _error ??= PubSubErrorInfo.From(ex);
                _firstFailedMessageId ??= _chunkFirstMessageId;
                StopReleasing(count);
            }
            finally
            {
                _ackIds.Clear();
                _chunkFirstMessageId = null;
            }
        }

        internal void Report(ILogger logger, string subscription)
        {
            if (_budgetExhausted)
            {
                LogBudgetExhausted(logger, subscription, _skipped, _firstFailedMessageId ?? string.Empty);
            }

            if (_error is { } error)
            {
                LogReleaseFailed(
                    logger, subscription, _skipped, _firstFailedMessageId ?? string.Empty,
                    error.ExceptionType, error.StatusCode);
            }
        }

        private void StopReleasing(int pendingCount)
        {
            _disposeOnly = true;
            _skipped += pendingCount;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub shutdown release budget exhausted for subscription '{SubscriptionName}': " +
                  "{SkippedCount} message(s) were disposed without being made deliverable again " +
                  "(first MessageId={MessageId}). They are redelivered after their ack deadline.")]
    private static partial void LogBudgetExhausted(
        ILogger logger, string subscriptionName, int skippedCount, string messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub shutdown release failed for subscription '{SubscriptionName}': {SkippedCount} " +
                  "message(s) were disposed without being made deliverable again (first MessageId={MessageId}, " +
                  "{ExceptionType}, StatusCode={StatusCode}). They are redelivered after their ack deadline.")]
    private static partial void LogReleaseFailed(
        ILogger logger, string subscriptionName, int skippedCount, string messageId,
        string exceptionType, string statusCode);
}
