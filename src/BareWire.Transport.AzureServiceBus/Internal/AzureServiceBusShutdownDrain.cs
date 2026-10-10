using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// Consumer shutdown helpers: reading the buffer only while the consumer token is live, and handing messages
/// the caller never received back to Service Bus (<c>AbandonMessageAsync</c>).
/// </summary>
/// <remarks>
/// All release paths of one consumer share one <see cref="AzureServiceBusShutdownBudget"/>; once it is spent,
/// or after the first failed call, the remaining messages are only disposed and evicted (their locks are
/// released when the receiver is closed or expire). Nothing here throws, and nothing logs lock tokens or
/// exception messages.
/// </remarks>
internal static partial class AzureServiceBusShutdownDrain
{
    /// <summary>Upper bound of concurrent <c>AbandonMessageAsync</c> calls issued by one release run.</summary>
    internal const int MaxParallelAbandons = 16;

    /// <summary>Longest a stopping consumer waits for its receive loop to observe cancellation.</summary>
    internal static readonly TimeSpan ReceiveStopTimeout = TimeSpan.FromSeconds(5);

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
    /// Empties the buffer: evicts every registry entry and disposes every message. When
    /// <paramref name="abandon"/> is set, the messages are also abandoned (within the budget).
    /// </summary>
    internal static async Task DrainAsync(
        ChannelReader<InboundMessage> reader,
        AzureServiceBusConsumerRegistry registry,
        string consumerId,
        string endpointName,
        bool abandon,
        AzureServiceBusShutdownBudget budget,
        ILogger logger)
    {
        var releaser = new Releaser(budget);

        while (reader.TryRead(out InboundMessage? message))
        {
            string messageId = message.MessageId;
            (ServiceBusReceivedMessage Message, ServiceBusReceiver Receiver)? entry =
                registry.TryEvictMessage(consumerId, message.DeliveryTag);
            message.Dispose();

            if (abandon && entry is not null)
            {
                releaser.Add(entry.Value.Receiver, entry.Value.Message, messageId);
            }
        }

        await releaser.RunAsync().ConfigureAwait(false);
        releaser.Report(logger, endpointName);
    }

    /// <summary>
    /// Abandons messages that were received but never registered or buffered (the consumer was already
    /// stopping when they arrived).
    /// </summary>
    internal static async Task AbandonUnregisteredAsync(
        ServiceBusReceiver receiver,
        IReadOnlyList<ServiceBusReceivedMessage> messages,
        int startIndex,
        string endpointName,
        AzureServiceBusShutdownBudget budget,
        ILogger logger)
    {
        var releaser = new Releaser(budget);

        for (int i = startIndex; i < messages.Count; i++)
        {
            releaser.Add(receiver, messages[i], messages[i].MessageId);
        }

        await releaser.RunAsync().ConfigureAwait(false);
        releaser.Report(logger, endpointName);
    }

    /// <summary>Abandons a batch of messages with bounded parallelism, never throwing.</summary>
    private sealed class Releaser(AzureServiceBusShutdownBudget budget)
    {
        private readonly List<(ServiceBusReceiver Receiver, ServiceBusReceivedMessage Message, string MessageId)> _items = [];
        private readonly Lock _gate = new();

        private int _released;
        private int _disposeOnly;
        private bool _budgetExhausted;
        private AzureServiceBusErrorInfo? _error;
        private string? _firstFailedMessageId;

        internal void Add(ServiceBusReceiver receiver, ServiceBusReceivedMessage message, string messageId) =>
            _items.Add((receiver, message, messageId));

        internal async Task RunAsync()
        {
            if (_items.Count == 0)
            {
                return;
            }

            CancellationToken token = budget.Token;

            try
            {
                await Parallel.ForEachAsync(
                    _items,
                    new ParallelOptions { MaxDegreeOfParallelism = MaxParallelAbandons, CancellationToken = token },
                    AbandonOneAsync).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Budget spent before every item was started; the unstarted ones are only disposed.
                MarkBudgetExhausted(_items[0].MessageId);
            }
        }

        internal void Report(ILogger logger, string endpointName)
        {
            int skipped = _items.Count - Volatile.Read(ref _released);

            if (_budgetExhausted)
            {
                LogBudgetExhausted(logger, endpointName, skipped, _firstFailedMessageId ?? string.Empty);
            }

            if (_error is { } error)
            {
                LogReleaseFailed(
                    logger, endpointName, skipped, _firstFailedMessageId ?? string.Empty,
                    error.ExceptionType, error.FailureReason);
            }
        }

        private async ValueTask AbandonOneAsync(
            (ServiceBusReceiver Receiver, ServiceBusReceivedMessage Message, string MessageId) item,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposeOnly) != 0)
            {
                return;
            }

            try
            {
                await item.Receiver
                    .AbandonMessageAsync(item.Message, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _released);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkBudgetExhausted(item.MessageId);
            }
            catch (Exception ex)
            {
                // Handled by design: shutdown must never throw. Record only loggable facts and fall back
                // to dispose-only for the rest of the drain.
                lock (_gate)
                {
                    _error ??= AzureServiceBusErrorInfo.From(ex);
                    _firstFailedMessageId ??= item.MessageId;
                }

                Volatile.Write(ref _disposeOnly, 1);
            }
        }

        private void MarkBudgetExhausted(string messageId)
        {
            lock (_gate)
            {
                _budgetExhausted = true;
                _firstFailedMessageId ??= messageId;
            }

            Volatile.Write(ref _disposeOnly, 1);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus shutdown release budget exhausted for queue '{QueueName}': {SkippedCount} " +
                  "message(s) were not abandoned (first MessageId={MessageId}). " +
                  "Their locks are released when the receiver closes or expire.")]
    private static partial void LogBudgetExhausted(
        ILogger logger, string queueName, int skippedCount, string messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus shutdown release failed for queue '{QueueName}': {SkippedCount} message(s) " +
                  "were not abandoned (first MessageId={MessageId}, {ExceptionType}, Reason={FailureReason}). " +
                  "Their locks are released when the receiver closes or expire.")]
    private static partial void LogReleaseFailed(
        ILogger logger, string queueName, int skippedCount, string messageId,
        string exceptionType, string failureReason);
}
