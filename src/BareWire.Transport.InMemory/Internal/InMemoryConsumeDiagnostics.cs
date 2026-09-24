using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Logging and metrics for the in-memory consume path: reports deliveries that were handed to a
/// consumer, never settled, and dropped because the transport shut down, as well as deliveries dropped
/// during settlement (dead-letter fan-out with no accepting target, a redelivery limit reached with no
/// dead-letter exchange, and similar). The log side is temporary and minimal — a later subtask folds it
/// into the transport's final logging/metrics shape.
/// </summary>
internal sealed partial class InMemoryConsumeDiagnostics
{
    /// <summary>The name of the counter of deliveries dropped on shutdown.</summary>
    internal const string DroppedOnShutdownCounterName = "barewire.inmemory.deliveries.dropped_on_shutdown";

    /// <summary>The name of the counter of deliveries dropped during settlement.</summary>
    internal const string SettlementDroppedCounterName = "barewire.inmemory.settlement.dropped";

    private static readonly TimeSpan SettlementDropLogWindow = TimeSpan.FromSeconds(60);
    private static readonly int SettlementDropReasonCount = Enum.GetValues<SettlementDropReason>().Length;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    // Opt-in counters — created only when an external Meter is supplied by the composition root.
    private readonly Counter<long>? _droppedOnShutdownCounter;
    private readonly Counter<long>? _settlementDroppedCounter;
    private long _droppedOnShutdownCount;
    private long _disposedUnsettledCount;
    private readonly long[] _settlementDroppedCounts = new long[SettlementDropReasonCount];

    // Per-(queue, reason) log throttle state, populated lazily — queue names come from the sealed
    // topology, so this dictionary's key set is bounded by the number of declared queues.
    private readonly ConcurrentDictionary<string, ThrottleState[]> _settlementThrottle = new(StringComparer.Ordinal);

    /// <param name="logger">The logger to report dropped deliveries to. Must not be <see langword="null"/>.</param>
    /// <param name="meter">An optional meter; when supplied, dropped deliveries are also counted on it.</param>
    /// <param name="timeProvider">The time source used to throttle settlement-drop logs. Defaults to <see cref="TimeProvider.System"/>.</param>
    internal InMemoryConsumeDiagnostics(ILogger logger, Meter? meter = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _droppedOnShutdownCounter = meter?.CreateCounter<long>(
            DroppedOnShutdownCounterName,
            unit: "{delivery}",
            description: "Number of in-memory deliveries handed to a consumer, never settled, and dropped " +
                "because the transport shut down. Tagged with the queue name.");
        _settlementDroppedCounter = meter?.CreateCounter<long>(
            SettlementDroppedCounterName,
            unit: "{delivery}",
            description: "PROVISIONAL — number of in-memory deliveries dropped during settlement. Tagged " +
                "with the queue name and the drop reason.");
    }

    /// <summary>Gets the total number of deliveries dropped on shutdown so far.</summary>
    internal long DroppedOnShutdownCount => Interlocked.Read(ref _droppedOnShutdownCount);

    /// <summary>
    /// Records that <paramref name="count"/> unsettled deliveries of <paramref name="queueName"/> were
    /// dropped because the transport shut down.
    /// </summary>
    internal void DeliveriesDroppedOnShutdown(string queueName, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _droppedOnShutdownCount, count);
        _droppedOnShutdownCounter?.Add(count, new KeyValuePair<string, object?>("queue", queueName));
        LogDroppedOnShutdown(_logger, count, queueName);
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
        LogDisposedUnsettled(_logger, count, queueName);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "{Count} in-memory delivery(ies) on queue '{QueueName}' were disposed by the consumer without being " +
        "settled; their bodies were already released, so they were dropped instead of being requeued.")]
    private static partial void LogDisposedUnsettled(ILogger logger, int count, string queueName);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "In-memory transport shut down with {Count} unsettled delivery(ies) on queue '{QueueName}'; they were " +
        "dropped and their queue slots released. In-memory delivery is not durable across shutdown.")]
    private static partial void LogDroppedOnShutdown(ILogger logger, int count, string queueName);

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
        _settlementDroppedCounter?.Add(
            count,
            new KeyValuePair<string, object?>("queue", queueName),
            new KeyValuePair<string, object?>("reason", reason.ToTagValue()));

        ThrottleState[] states = _settlementThrottle.GetOrAdd(queueName, static _ => CreateThrottleStates());
        if (!states[(int)reason].TryEnterLogWindow(_timeProvider, SettlementDropLogWindow, out int suppressedCount))
        {
            return;
        }

        LogSettlementDropped(_logger, count, queueName, reason.ToTagValue(), suppressedCount);
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
    /// already holds a reserved slot. Logged at <see cref="LogLevel.Error"/>, never throttled: this is an
    /// exceptional path, not a routine drop.
    /// </summary>
    internal void DeferredRedeliveryFailed(string queueName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(queueName);
        ArgumentNullException.ThrowIfNull(exception);

        LogDeferredRedeliveryFailed(_logger, queueName, exception);
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
