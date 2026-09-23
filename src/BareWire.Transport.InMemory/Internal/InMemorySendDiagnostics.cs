using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Logging and metrics for the in-memory send path: a rejected-copy/message counter, and throttled,
/// aggregated logs — never one log entry per message. The throttle keys are a fixed, frozen table built
/// once from the topology's declared queue and exchange names (plus an empty-name bucket for reasons
/// that never carry a name) — a publisher-supplied name never becomes a throttle key or a metric tag; see
/// <see cref="MessageRejected"/>.
/// </summary>
internal sealed partial class InMemorySendDiagnostics
{
    /// <summary>The name of the rejected copies/messages counter. PROVISIONAL — may later be folded into a shared rejection counter.</summary>
    internal const string RejectedCounterName = "barewire.inmemory.send.rejected";

    private const int MaxLoggedNameLength = 256;
    private static readonly TimeSpan LogWindow = TimeSpan.FromSeconds(60);
    private static readonly int ReasonCount = Enum.GetValues<SendRejectionReason>().Length;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly FrozenDictionary<string, ThrottleState[]> _throttleTable;

    // Opt-in, dimensionless-on-creation counter — created only when an external Meter is supplied by the
    // composition root. null = no instrument, no cost.
    private readonly Counter<long>? _rejectedCounter;

    private long _rejectedCount;
    private long _logFailureCount;

    /// <param name="logger">The logger to report rejections to. Must not be <see langword="null"/>.</param>
    /// <param name="registry">
    /// The sealed topology snapshot this send path routes against — its declared queue and exchange
    /// names seed the frozen log-throttle table built once here. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="meter">An optional meter; when supplied, rejections are also counted on it.</param>
    /// <param name="timeProvider">The time source for log throttling. Defaults to <see cref="TimeProvider.System"/>.</param>
    internal InMemorySendDiagnostics(
        ILogger logger, ExchangeRegistry registry, Meter? meter = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(registry);

        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _throttleTable = BuildThrottleTable(registry);

        // The instrument name/shape is provisional — see RejectedCounterName.
        _rejectedCounter = meter?.CreateCounter<long>(
            RejectedCounterName,
            unit: "{message}",
            description: "PROVISIONAL — number of in-memory send copies/messages rejected before or during " +
                "admission. Tagged with 'reason' always, and either 'queue' or 'exchange' when applicable — " +
                "the exchange tag is present only when the exchange is declared in the topology.");
    }

    /// <summary>Gets the total number of rejected copies/messages recorded so far, regardless of the meter.</summary>
    internal long RejectedCount => Interlocked.Read(ref _rejectedCount);

    /// <summary>Gets the number of logger calls from this instance that threw and were suppressed.</summary>
    internal long LogFailureCount => Interlocked.Read(ref _logFailureCount);

    /// <summary>
    /// Records that one fan-out copy of an otherwise-accepted message was rejected because its target
    /// queue was full (latched, or full and the wait for it failed). Metric only — a per-copy log would
    /// violate the "never log per message" rule; see <see cref="QueueLatchedAfterWait"/> for the one
    /// aggregated log this path can trigger.
    /// </summary>
    /// <param name="queueName">The target queue's declared name.</param>
    /// <param name="reason">The rejection reason (expected to be <see cref="SendRejectionReason.QueueFull"/>).</param>
    internal void CopyRejected(string queueName, SendRejectionReason reason)
    {
        ArgumentNullException.ThrowIfNull(queueName);

        Interlocked.Increment(ref _rejectedCount);
        _rejectedCounter?.Add(1,
            new KeyValuePair<string, object?>("reason", reason.ToTag()),
            new KeyValuePair<string, object?>("queue", queueName));
    }

    /// <summary>
    /// Records that an entire message was rejected before or during routing (validation failure or an
    /// unexpected exception). Increments the metric first, then emits a throttled <see cref="LogLevel.Error"/>
    /// log — never body or headers, only metadata truncated to <see cref="MaxLoggedNameLength"/> characters.
    /// </summary>
    /// <param name="reason">The rejection reason.</param>
    /// <param name="declaredExchange">
    /// The exchange name, only when it is declared in the topology (<c>ExchangeRegistry.ContainsExchange</c>
    /// returned <see langword="true"/>) — becomes the metric's <c>exchange</c> tag and the log's exchange
    /// field. <see langword="null"/> when the exchange is undeclared, unresolved, or not yet known.
    /// </param>
    /// <param name="publisherExchange">
    /// The exchange name as supplied by the publisher or resolved from options, used only for the log
    /// text when <paramref name="declaredExchange"/> is <see langword="null"/> — never a metric tag or a
    /// throttle key (unbounded, publisher-controlled cardinality).
    /// </param>
    /// <param name="routingKey">The message's routing key, truncated for the log.</param>
    /// <param name="bodyLength">The message body's length in bytes, for the log.</param>
    /// <param name="exception">The exception that caused the rejection, when the reason is an internal error.</param>
    internal void MessageRejected(
        SendRejectionReason reason,
        string? declaredExchange,
        string? publisherExchange,
        string routingKey,
        int bodyLength,
        Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(routingKey);

        Interlocked.Increment(ref _rejectedCount);
        if (declaredExchange is not null)
        {
            _rejectedCounter?.Add(1,
                new KeyValuePair<string, object?>("reason", reason.ToTag()),
                new KeyValuePair<string, object?>("exchange", declaredExchange));
        }
        else
        {
            _rejectedCounter?.Add(1, new KeyValuePair<string, object?>("reason", reason.ToTag()));
        }

        string scope = declaredExchange ?? string.Empty;
        if (!TryEnterLogWindow(scope, reason, out int suppressedCount))
        {
            return;
        }

        string exchangeForLog = Truncate(declaredExchange ?? publisherExchange ?? string.Empty, MaxLoggedNameLength);
        string routingKeyForLog = Truncate(routingKey, MaxLoggedNameLength);

        try
        {
            LogMessageRejected(_logger, reason.ToTag(), exchangeForLog, routingKeyForLog, bodyLength, suppressedCount, exception);
        }
        catch (Exception)
        {
            // A logging provider's own failure must never fail a send call, and must never re-throw
            // after messages ahead of this one have already been (partially) admitted — see the send
            // path's per-message exception boundary. The metric increment above already happened.
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    /// <summary>
    /// Records that <paramref name="count"/> messages were never processed because the adapter was
    /// closed, or because the call's wait was cancelled. Metric (reason only — no name has any meaning
    /// for a batch of unprocessed messages) plus one throttled log: <see cref="LogLevel.Warning"/> for
    /// <see cref="SendRejectionReason.Closed"/>, <see cref="LogLevel.Debug"/> for
    /// <see cref="SendRejectionReason.Cancelled"/>.
    /// </summary>
    internal void MessagesSkipped(SendRejectionReason reason, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _rejectedCount, count);
        _rejectedCounter?.Add(count, new KeyValuePair<string, object?>("reason", reason.ToTag()));

        if (!TryEnterLogWindow(string.Empty, reason, out int suppressedCount))
        {
            return;
        }

        try
        {
            if (reason == SendRejectionReason.Closed)
            {
                LogMessagesSkippedClosed(_logger, count, suppressedCount);
            }
            else
            {
                LogMessagesSkippedCancelled(_logger, count, suppressedCount);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    /// <summary>
    /// Records that <paramref name="queue"/> was latched because the call's one wait for it failed
    /// (timed out). Throttled <see cref="LogLevel.Warning"/> — the latch a full queue sets on its own,
    /// with no active consumer, never logs here (indistinguishable per message; see the send algorithm's
    /// design notes).
    /// </summary>
    internal void QueueLatchedAfterWait(InMemoryQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        if (!TryEnterLogWindow(queue.Name, SendRejectionReason.QueueFull, out int suppressedCount))
        {
            return;
        }

        try
        {
            LogQueueLatchedAfterWait(_logger, queue.Name, queue.Occupancy, queue.Capacity, suppressedCount);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    private bool TryEnterLogWindow(string scope, SendRejectionReason reason, out int suppressedCount)
    {
        if (!_throttleTable.TryGetValue(scope, out ThrottleState[]? states))
        {
            // Defensive fallback — every scope this type is called with is either a declared name (queue
            // or exchange) or the empty-name bucket, both seeded at construction; this should be
            // unreachable in practice.
            _throttleTable.TryGetValue(string.Empty, out states);
        }

        if (states is null)
        {
            suppressedCount = 0;
            return true;
        }

        return states[(int)reason].TryEnterLogWindow(_timeProvider, LogWindow, out suppressedCount);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static FrozenDictionary<string, ThrottleState[]> BuildThrottleTable(ExchangeRegistry registry)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        foreach (string queueName in registry.Queues.Keys)
        {
            scopes.Add(queueName);
        }

        foreach (string exchangeName in registry.Exchanges.Keys)
        {
            scopes.Add(exchangeName);
        }

        var table = new Dictionary<string, ThrottleState[]>(scopes.Count, StringComparer.Ordinal);
        foreach (string scope in scopes)
        {
            var states = new ThrottleState[ReasonCount];
            for (int i = 0; i < ReasonCount; i++)
            {
                states[i] = new ThrottleState();
            }

            table[scope] = states;
        }

        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Error, Message =
        "In-memory send rejected a message: reason '{Reason}', exchange '{Exchange}', routing key " +
        "'{RoutingKey}', body length {BodyLength} byte(s). {SuppressedCount} earlier occurrence(s) for " +
        "this exchange were suppressed since the last log entry.")]
    private static partial void LogMessageRejected(
        ILogger logger, string reason, string exchange, string routingKey, int bodyLength, int suppressedCount,
        Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "In-memory transport rejected {Count} message(s) because the adapter was closed. {SuppressedCount} " +
        "earlier occurrence(s) were suppressed since the last log entry.")]
    private static partial void LogMessagesSkippedClosed(ILogger logger, int count, int suppressedCount);

    [LoggerMessage(Level = LogLevel.Debug, Message =
        "In-memory transport rejected {Count} message(s) because the send call was cancelled. " +
        "{SuppressedCount} earlier occurrence(s) were suppressed since the last log entry.")]
    private static partial void LogMessagesSkippedCancelled(ILogger logger, int count, int suppressedCount);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "In-memory queue '{QueueName}' latched after a failed wait for space: occupancy {Occupancy} of " +
        "{Capacity}. {SuppressedCount} earlier occurrence(s) for this queue were suppressed since the " +
        "last log entry.")]
    private static partial void LogQueueLatchedAfterWait(
        ILogger logger, string queueName, int occupancy, int capacity, int suppressedCount);

    /// <summary>
    /// Per-(scope, reason) log throttle state: logs at most once per <see cref="LogWindow"/>, reporting
    /// how many occurrences were suppressed since the previous log entry. Mirrors
    /// <c>InMemoryRouter</c>'s per-exchange unroutable throttle.
    /// </summary>
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
