using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Logging and metrics for the in-memory consume path: reports deliveries that were handed to a
/// consumer, never settled, and dropped because the transport shut down, as well as deliveries dropped
/// during settlement (dead-letter fan-out with no accepting target, a redelivery limit reached with no
/// dead-letter exchange, and similar). Every drop is reported to the shared
/// <see cref="InMemoryTransportMetrics"/> owner — this type creates no instrument of its own.
/// </summary>
internal sealed partial class InMemoryConsumeDiagnostics
{
    private static readonly TimeSpan SettlementDropLogWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownDropLogWindow = TimeSpan.FromSeconds(60);
    private static readonly int SettlementDropReasonCount = Enum.GetValues<SettlementDropReason>().Length;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    // Opt-in — created only when an external Meter is supplied by the composition root, through the
    // adapter's single InMemoryTransportMetrics owner. null = no instrument, no cost.
    private readonly InMemoryTransportMetrics? _metrics;

    private long _droppedOnShutdownCount;
    private long _disposedUnsettledCount;
    private long _drainDroppedCount;
    private long _logFailureCount;
    private readonly long[] _settlementDroppedCounts = new long[SettlementDropReasonCount];

    // Per-(queue, reason) log throttle state, populated lazily — queue names come from the sealed
    // topology, so this dictionary's key set is bounded by the number of declared queues.
    private readonly ConcurrentDictionary<string, ThrottleState[]> _settlementThrottle = new(StringComparer.Ordinal);

    // Per-queue log throttle state for DeliveriesDroppedOnShutdown — populated lazily, same bound as above.
    private readonly ConcurrentDictionary<string, ThrottleState> _shutdownDropThrottle = new(StringComparer.Ordinal);

    /// <param name="logger">The logger to report dropped deliveries to. Must not be <see langword="null"/>.</param>
    /// <param name="metrics">
    /// The adapter's single instrument owner; when supplied (non-<see langword="null"/>, built from a
    /// non-<see langword="null"/> meter), dropped deliveries are also counted on it.
    /// </param>
    /// <param name="timeProvider">The time source used to throttle settlement-drop logs. Defaults to <see cref="TimeProvider.System"/>.</param>
    internal InMemoryConsumeDiagnostics(ILogger logger, InMemoryTransportMetrics? metrics = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics;
    }

    /// <summary>Gets the total number of deliveries dropped on shutdown so far.</summary>
    internal long DroppedOnShutdownCount => Interlocked.Read(ref _droppedOnShutdownCount);

    /// <summary>
    /// Gets the number of metric or logger calls from this instance that threw and were suppressed — see
    /// the explicit catch-and-count guard on every method below. A throwing <c>MeterListener</c> callback
    /// or logging provider must never crash the caller, including the defer scheduler's own timer thread
    /// (see <see cref="DeferredRedeliveryFailed"/>).
    /// </summary>
    internal long LogFailureCount => Interlocked.Read(ref _logFailureCount);

    /// <summary>
    /// Records that <paramref name="count"/> unsettled deliveries of <paramref name="queueName"/> were
    /// dropped because the transport shut down. The counter and the metric are updated on every call —
    /// several individual calls (one per delivery) can happen in a short burst from the deferred-redelivery
    /// paths racing shutdown — but the <see cref="LogLevel.Warning"/> log is throttled per queue so a burst
    /// never produces one log entry per message; the adapter's own aggregated per-queue call at the end of
    /// <c>Dispose</c> is unaffected in the common case, since it is normally the first (and only) call for
    /// that queue.
    /// </summary>
    internal void DeliveriesDroppedOnShutdown(string queueName, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _droppedOnShutdownCount, count);

        // A throwing metrics listener or logging provider must never propagate out of Dispose — caught
        // and counted explicitly instead, the same pattern InMemorySendDiagnostics uses for its own log
        // calls.
        try
        {
            _metrics?.RecordQueueRejected("drain_dropped", queueName, count);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }

        ThrottleState state = _shutdownDropThrottle.GetOrAdd(queueName, static _ => new ThrottleState());
        if (!state.TryEnterLogWindow(_timeProvider, ShutdownDropLogWindow, out int suppressedCount))
        {
            return;
        }

        try
        {
            LogDroppedOnShutdown(_logger, count, queueName, suppressedCount);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    /// <summary>Gets the total number of deliveries dropped because their message was disposed unsettled.</summary>
    internal long DisposedUnsettledCount => Interlocked.Read(ref _disposedUnsettledCount);

    /// <summary>
    /// Records that <paramref name="count"/> deliveries of <paramref name="queueName"/> could not be requeued
    /// because the consumer disposed their messages without settling them, and were dropped.
    /// </summary>
    internal void DeliveriesDisposedUnsettled(string queueName, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _disposedUnsettledCount, count);

        try
        {
            LogDisposedUnsettled(_logger, count, queueName);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "{Count} in-memory delivery(ies) on queue '{QueueName}' were disposed by the consumer without being " +
        "settled; their bodies were already released, so they were dropped instead of being requeued.")]
    private static partial void LogDisposedUnsettled(ILogger logger, int count, string queueName);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "In-memory transport shut down with {Count} unsettled delivery(ies) on queue '{QueueName}'; they were " +
        "dropped and their queue slots released. In-memory delivery is not durable across shutdown. " +
        "{SuppressedCount} earlier occurrence(s) for this queue were suppressed since the last log entry.")]
    private static partial void LogDroppedOnShutdown(ILogger logger, int count, string queueName, int suppressedCount);

    /// <summary>Gets the total number of undelivered messages dropped on drain (queue close) so far.</summary>
    internal long DrainDroppedCount => Interlocked.Read(ref _drainDroppedCount);

    /// <summary>
    /// Records that <paramref name="count"/> undelivered messages of <paramref name="queueName"/> were
    /// dropped from that queue's channel because the transport was disposed while they were still sitting
    /// there, never handed to a consumer. A no-op when <paramref name="count"/> is not positive. Called
    /// once per queue, at the very end of <c>Dispose</c>'s own shutdown sequence, so it reports a single
    /// aggregated warning rather than one per dropped message.
    /// </summary>
    internal void DeliveriesDroppedOnDrain(string queueName, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _drainDroppedCount, count);

        try
        {
            _metrics?.RecordQueueRejected("drain_dropped", queueName, count);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }

        try
        {
            LogDrainDropped(_logger, count, queueName);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "In-memory transport was disposed with {Count} undelivered message(s) on queue '{QueueName}'; they " +
        "were dropped and their buffers returned to the pool. In-memory delivery is not durable across shutdown.")]
    private static partial void LogDrainDropped(ILogger logger, int count, string queueName);

    /// <summary>Gets the total number of deliveries dropped during settlement for <paramref name="reason"/>.</summary>
    internal long SettlementDroppedCount(SettlementDropReason reason) =>
        Interlocked.Read(ref _settlementDroppedCounts[(int)reason]);

    /// <summary>
    /// Records that <paramref name="count"/> deliveries of <paramref name="queueName"/> were dropped during
    /// settlement for <paramref name="reason"/>: increments the total and the opt-in metric (never
    /// throttled — one measurement per drop), then emits a <see cref="LogLevel.Warning"/> log throttled per
    /// (queue, reason) pair so a "poison message" burst with no dead-letter exchange cannot flood the log
    /// sink. Metadata only — never the delivery's body or headers.
    /// </summary>
    internal void SettlementDropped(string queueName, SettlementDropReason reason, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(queueName);

        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _settlementDroppedCounts[(int)reason], count);

        try
        {
            _metrics?.RecordQueueRejected(reason.ToTagValue(), queueName, count);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }

        ThrottleState[] states = _settlementThrottle.GetOrAdd(queueName, static _ => CreateThrottleStates());
        if (!states[(int)reason].TryEnterLogWindow(_timeProvider, SettlementDropLogWindow, out int suppressedCount))
        {
            return;
        }

        try
        {
            LogSettlementDropped(_logger, count, queueName, reason.ToTagValue(), suppressedCount);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    private static ThrottleState[] CreateThrottleStates()
    {
        var states = new ThrottleState[SettlementDropReasonCount];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = new ThrottleState();
        }

        return states;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "{Count} in-memory delivery(ies) on queue '{QueueName}' were dropped during settlement (reason: " +
        "'{Reason}'). {SuppressedCount} earlier occurrence(s) for this queue and reason were suppressed " +
        "since the last log entry.")]
    private static partial void LogSettlementDropped(
        ILogger logger, int count, string queueName, string reason, int suppressedCount);

    /// <summary>
    /// Per-(queue, reason) log throttle state: logs at most once per <see cref="SettlementDropLogWindow"/>,
    /// reporting how many occurrences were suppressed since the previous log entry. Mirrors
    /// <c>InMemorySendDiagnostics</c>'s and <c>InMemoryRouter</c>'s own per-scope throttle states.
    /// </summary>
    /// <summary>
    /// Records that a deferred redelivery to <paramref name="queueName"/> failed because the target
    /// queue's channel rejected the write — an invariant violation, since a deferred redelivery always
    /// already holds a reserved slot. Increments the shared rejected-messages metric with reason
    /// <c>internal_error</c> BEFORE logging, then logs at <see cref="LogLevel.Error"/>, never throttled:
    /// this is an exceptional path, not a routine drop.
    /// </summary>
    internal void DeferredRedeliveryFailed(string queueName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(queueName);
        ArgumentNullException.ThrowIfNull(exception);

        // Runs on the defer scheduler's own timer thread: a throwing metrics listener or logging provider
        // must never escape here, or it would crash that background thread and silently stop future
        // deferred redeliveries. Caught and counted explicitly instead of left to propagate.
        try
        {
            _metrics?.RecordQueueRejected("internal_error", queueName);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }

        try
        {
            LogDeferredRedeliveryFailed(_logger, queueName, exception);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Deferred redelivery to in-memory queue '{QueueName}' failed: the channel rejected the write. " +
        "The delivery was dropped instead of being redelivered.")]
    private static partial void LogDeferredRedeliveryFailed(ILogger logger, string queueName, Exception exception);

    private sealed class ThrottleState
    {
        private long _lastLogTicks;
        private int _suppressedSinceLastLog;

        internal bool TryEnterLogWindow(TimeProvider timeProvider, TimeSpan window, out int suppressedCount)
        {
            long now = timeProvider.GetUtcNow().UtcTicks;
            long last = Volatile.Read(ref _lastLogTicks);

            if (last != 0 && now - last < window.Ticks)
            {
                Interlocked.Increment(ref _suppressedSinceLastLog);
                suppressedCount = 0;
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastLogTicks, now, last) != last)
            {
                Interlocked.Increment(ref _suppressedSinceLastLog);
                suppressedCount = 0;
                return false;
            }

            suppressedCount = Interlocked.Exchange(ref _suppressedSinceLastLog, 0);
            return true;
        }
    }
}
