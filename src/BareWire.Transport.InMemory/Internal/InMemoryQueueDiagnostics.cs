using System.Collections.Frozen;
using System.Collections.Immutable;
using BareWire.Abstractions;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The single <see cref="IInMemoryQueueLatchObserver"/> implementation for this package: turns every
/// latch episode into exactly one throttled <see cref="LogLevel.Warning"/> (on open) plus, only for an
/// episode whose warning was actually logged, exactly one <see cref="LogLevel.Information"/> (on close),
/// and reports the shared <see cref="InMemoryTransportMetrics.LatchEpisodesCounterName"/> counter for
/// every episode regardless of whether its warning was throttled away. Also owns
/// <see cref="ReportHealth"/>: <see cref="InMemoryTransportAdapter.GetHealth"/> calls it once per queue,
/// on every health check, but this type logs a queue's health transition at most once per direction
/// (hysteresis — see that method's remarks), never once per poll.
/// </summary>
/// <remarks>
/// <para>
/// <b>Race-safe open/close hand-off.</b> <see cref="InMemoryQueue"/> guarantees exactly one
/// <see cref="OnLatchSet"/> and exactly one matching <see cref="OnLatchCleared"/> per episode, but the two
/// calls can arrive on different threads in either order relative to each OTHER's processing — a very
/// short episode's close can, in principle, be observed here before its open has finished running. Each
/// <see cref="InMemoryLatchEpisode"/> carries an <see cref="InMemoryLatchEpisode.ObserverState"/> field
/// this type alone owns, with three reachable states: pending (the initial value), logged (the open's
/// warning was actually emitted, not throttled away), and everything else meaning "do not log the
/// matching information" — either the open's warning was itself throttled away (suppressed), or the close
/// ran first and marked the episode closed-before-open so the (later) open never logs at all. Every
/// transition is a single <see cref="Interlocked.CompareExchange(ref int, int, int)"/> from the pending
/// state, so whichever side reaches it first always wins, and the loser's own decision (log, or don't)
/// is fully determined by what it reads back.
/// </para>
/// <para>
/// <b>F3 — never throws.</b> The entire body of <see cref="OnLatchSet"/>, <see cref="OnLatchCleared"/>,
/// and <see cref="ReportHealth"/> — timestamp reads, state lookups, the metric call, and the log call — is
/// wrapped in one explicit <c>catch (Exception)</c> that counts the failure instead of re-throwing: all
/// three run on paths that must never fail their caller, including inside <see cref="InMemoryQueue.ReleaseSlot"/>
/// on a dispose path, before a buffer is returned to its pool.
/// </para>
/// </remarks>
internal sealed partial class InMemoryQueueDiagnostics : IInMemoryQueueLatchObserver
{
    // Episode-warning throttle state, per queue: at most one Warning per this window, with the count of
    // episodes suppressed since the last one logged carried into the next.
    private static readonly TimeSpan LatchWarningWindow = TimeSpan.FromSeconds(60);

    // Episode observer-state values — see this type's remarks on the race-safe open/close hand-off.
    private const int EpisodePending = 0;
    private const int EpisodeLogged = 1;
    private const int EpisodeSuppressed = 2;
    private const int EpisodeClosedBeforeOpen = 3;

    // Health hysteresis states — see ReportHealth's remarks.
    private const int HealthNotDegraded = 0;
    private const int HealthDegraded = 1;

    private readonly ILogger _logger;
    private readonly InMemoryTransportMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly FrozenDictionary<string, QueueState> _queueStates;
    private long _logFailureCount;

    /// <param name="logger">The logger latch-episode and health-transition entries are reported to. Must not be <see langword="null"/>.</param>
    /// <param name="queues">
    /// The broker's declared queues — seeds the per-queue throttle and hysteresis state this type keeps.
    /// Must not contain two queues with the same name.
    /// </param>
    /// <param name="metrics">The adapter's single instrument owner. Must not be <see langword="null"/>.</param>
    /// <param name="timeProvider">The time source for episode timestamps and log throttling. Defaults to <see cref="TimeProvider.System"/>.</param>
    internal InMemoryQueueDiagnostics(
        ILogger logger, ImmutableArray<InMemoryQueue> queues, InMemoryTransportMetrics metrics, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);

        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var states = new Dictionary<string, QueueState>(queues.Length, StringComparer.Ordinal);
        foreach (InMemoryQueue queue in queues)
        {
            states[queue.Name] = new QueueState();
        }

        _queueStates = states.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the number of metric or logger calls from this instance that threw and were suppressed — see
    /// this type's remarks on F3.
    /// </summary>
    internal long LogFailureCount => Interlocked.Read(ref _logFailureCount);

    /// <inheritdoc />
    public long GetTimestamp() => _timeProvider.GetTimestamp();

    /// <inheritdoc />
    public void OnLatchSet(InMemoryQueue queue, InMemoryLatchEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(episode);

        try
        {
            // Counted unthrottled — every episode counts, regardless of whether its Warning is logged.
            _metrics.RecordLatchEpisode(queue.Name);

            if (!_queueStates.TryGetValue(queue.Name, out QueueState? state))
            {
                return;
            }

            if (!state.LatchWarningThrottle.TryEnterLogWindow(_timeProvider, LatchWarningWindow, out int suppressedEpisodes))
            {
                Interlocked.CompareExchange(ref episode.ObserverState, EpisodeSuppressed, EpisodePending);
                return;
            }

            if (Interlocked.CompareExchange(ref episode.ObserverState, EpisodeLogged, EpisodePending) != EpisodePending)
            {
                // The close already ran first and marked this episode closed-before-open: this (late)
                // open must not log a Warning for an episode already reported as never having logged one.
                return;
            }

            LogLatchSet(_logger, queue.Name, queue.Occupancy, queue.Capacity, "queue_full", suppressedEpisodes);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    /// <inheritdoc />
    public void OnLatchCleared(InMemoryQueue queue, InMemoryLatchEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(episode);

        try
        {
            int priorState = Interlocked.CompareExchange(ref episode.ObserverState, EpisodeClosedBeforeOpen, EpisodePending);
            bool warningWasLogged = priorState == EpisodeLogged;
            if (!warningWasLogged)
            {
                // Either the open's Warning was itself suppressed by the throttle, or the open has not
                // run yet — this call just claimed that outcome for it (closed-before-open) — either way
                // no Information is logged for an episode whose Warning never appeared.
                return;
            }

            long rejectedCount = queue.RejectedCopyCount - episode.RejectedCopiesAtStart;
            long durationMs = (long)_timeProvider.GetElapsedTime(episode.StartTimestamp).TotalMilliseconds;
            LogLatchCleared(_logger, queue.Name, durationMs, rejectedCount);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    /// <summary>
    /// Reports <paramref name="queue"/>'s current health (<see cref="BusStatus.Degraded"/> once occupancy
    /// reaches at least 90% of capacity, <see cref="BusStatus.Healthy"/> otherwise) to
    /// <see cref="InMemoryTransportAdapter.GetHealth"/>'s caller and, only when it changes, logs the
    /// transition. Called on every health check — potentially very frequently — so the LOG uses
    /// hysteresis distinct from the REPORTED status: a <see cref="LogLevel.Warning"/> is logged the moment
    /// occupancy first reaches 90%, and the matching <see cref="LogLevel.Information"/> recovery is logged
    /// only once occupancy drops below 80% — the 80-90% band logs nothing in either direction, so a queue
    /// oscillating across the 90% line alone does not flood the log. Per-queue state is flipped with a
    /// single <see cref="Interlocked.CompareExchange(ref int, int, int)"/>, so concurrent polls log the
    /// transition exactly once. Never throws — see this type's remarks on F3.
    /// </summary>
    internal void ReportHealth(InMemoryQueue queue, int occupancy, int capacity)
    {
        ArgumentNullException.ThrowIfNull(queue);

        try
        {
            if (!_queueStates.TryGetValue(queue.Name, out QueueState? state))
            {
                return;
            }

            bool enteringDegraded = (long)occupancy * 10 >= (long)capacity * 9;
            bool recovering = (long)occupancy * 10 < (long)capacity * 8;

            if (enteringDegraded)
            {
                if (Interlocked.CompareExchange(ref state.HealthLogState, HealthDegraded, HealthNotDegraded) == HealthNotDegraded)
                {
                    LogQueueDegraded(_logger, queue.Name, occupancy, capacity);
                }
            }
            else if (recovering)
            {
                if (Interlocked.CompareExchange(ref state.HealthLogState, HealthNotDegraded, HealthDegraded) == HealthDegraded)
                {
                    LogQueueRecovered(_logger, queue.Name, occupancy, capacity);
                }
            }

            // The 80-89% hysteresis band: no state change, no log, regardless of the current state.
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    [LoggerMessage(EventId = 2301, Level = LogLevel.Warning, Message =
        "In-memory queue '{QueueName}' latched: occupancy {Occupancy} of {Capacity} (reason '{Reason}'); " +
        "further sends to it are rejected until occupancy drops below 50%. {SuppressedEpisodes} earlier " +
        "latch episode(s) for this queue were suppressed since the last log entry.")]
    private static partial void LogLatchSet(
        ILogger logger, string queueName, int occupancy, int capacity, string reason, int suppressedEpisodes);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Information, Message =
        "In-memory queue '{QueueName}' latch released after {DurationMs} ms; {RejectedCount} message " +
        "copy(ies) were rejected while it was latched.")]
    private static partial void LogLatchCleared(ILogger logger, string queueName, long durationMs, long rejectedCount);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Warning, Message =
        "In-memory queue '{QueueName}' health degraded: occupancy {Occupancy} of {Capacity} (at or above " +
        "90% of capacity).")]
    private static partial void LogQueueDegraded(ILogger logger, string queueName, int occupancy, int capacity);

    [LoggerMessage(EventId = 2304, Level = LogLevel.Information, Message =
        "In-memory queue '{QueueName}' health recovered: occupancy {Occupancy} of {Capacity} (below 80% " +
        "of capacity).")]
    private static partial void LogQueueRecovered(ILogger logger, string queueName, int occupancy, int capacity);

    /// <summary>Per-queue latch-warning throttle state plus health-transition hysteresis state.</summary>
    private sealed class QueueState
    {
        internal readonly ThrottleState LatchWarningThrottle = new();
        internal int HealthLogState = HealthNotDegraded;
    }

    /// <summary>
    /// Log throttle state: logs at most once per window, reporting how many occurrences were suppressed
    /// since the previous log entry. Mirrors <c>InMemorySendDiagnostics</c>'s, <c>InMemoryConsumeDiagnostics</c>'s,
    /// and <c>InMemoryRouter</c>'s own per-scope throttle states.
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
