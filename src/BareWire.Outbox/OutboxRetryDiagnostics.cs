using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox;

/// <summary>
/// Rate-limited observability for outbox rows released for a deferred retry after a transport nack: a
/// rate-limited Warning log entry, a counter of retried rows, and a lazily sampled gauge of the age of
/// the oldest due retry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Log fields.</b> The rate-limited entry carries only the id of one representative row, its
/// <c>RetryCount</c> (the number of nacks it has accumulated, including this one), how many rows of
/// the batch were released for retry, and how many retried rows since the previous entry were not
/// individually logged. It never carries the claim owner / instance identity, message body, headers,
/// routing key, or content type.
/// </para>
/// <para>
/// <b>Rate limiting.</b> The first call always logs. Within <see cref="LogWindow"/> of the last logged
/// entry, further calls only accumulate a suppressed-row count; the next call once the window has
/// elapsed logs again, carrying that suppressed count, and opens a new window. Time is read from the
/// <see cref="TimeProvider"/> supplied at construction. At most one Warning per <see cref="LogWindow"/>
/// is emitted per <see cref="OutboxDispatcher"/> instance.
/// </para>
/// <para>
/// <b>Metrics.</b> Both instruments are created only when a <see cref="Meter"/> is supplied to the
/// constructor; without one, the rate-limited log keeps working on its own.
/// <c>barewire.outbox.rows.retried</c> is a counter of rows released for a deferred retry — never
/// ordering-barrier siblings, which are not retries. <c>barewire.outbox.retry.oldest_due_age</c> is an
/// observable gauge, in seconds, of how long the oldest due-but-unclaimed retry has been waiting; it
/// reports 0 when no retry is currently due and reports no measurement at all before the first sample
/// is recorded. Neither instrument carries tags — the row id is not exposed as a tag value, to avoid
/// unbounded cardinality. The gauge is sampled lazily: see <see cref="TryConsumeAgeSampleRequest"/>.
/// </para>
/// </remarks>
internal sealed partial class OutboxRetryDiagnostics
{
    /// <summary>Name of the <see cref="Meter"/> instruments are created on — shared with the observability package by convention.</summary>
    internal const string MeterName = "BareWire";

    /// <summary>Name of the counter of outbox rows released for a deferred retry.</summary>
    internal const string RetriedRowsCounterName = "barewire.outbox.rows.retried";

    /// <summary>Name of the observable gauge reporting the age, in seconds, of the oldest due retry.</summary>
    internal const string OldestDueRetryAgeGaugeName = "barewire.outbox.retry.oldest_due_age";

    /// <summary>Minimum time between two consecutive Warning log entries for released retries.</summary>
    internal static readonly TimeSpan LogWindow = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Counter<long>? _retriedRows;

    private readonly Lock _logLock = new();
    private bool _hasLoggedOnce;
    private DateTimeOffset _windowStart;
    private long _suppressedCount;

    private long _retriedRowCount;

    // The sample is written by the dispatcher loop and read by the metrics collector, so it is published
    // as one 64-bit value (UTC ticks of the oldest due instant, or a sentinel) with Volatile semantics —
    // a multi-field or Nullable<DateTimeOffset> copy could be observed half-written.
    private const long NoSampleYet = long.MinValue;
    private const long NoRetryDue = -1;

    private int _ageSampleRequested;
    private long _oldestDueUtcTicks = NoSampleYet;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    /// <param name="logger">Logger the rate-limited retry entry and the probe-failure entry are written to.</param>
    /// <param name="timeProvider">Clock the log window and the gauge age are computed from.</param>
    /// <param name="meter">
    /// The <see cref="Meter"/> instruments are created on, or <see langword="null"/> to skip metrics
    /// entirely (the log still works). The meter is owned by whoever created it (typically an
    /// <see cref="IMeterFactory"/>, which caches instances by name) — this type never disposes it.
    /// </param>
    internal OutboxRetryDiagnostics(ILogger logger, TimeProvider timeProvider, Meter? meter = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        if (meter is not null)
        {
            _retriedRows = meter.CreateCounter<long>(
                RetriedRowsCounterName,
                unit: "{row}",
                description: "Number of outbox rows released for a deferred retry after a transport rejection.");

            meter.CreateObservableGauge(
                OldestDueRetryAgeGaugeName,
                ObserveOldestDueRetryAge,
                unit: "s",
                description: "Age, in seconds, of the oldest outbox retry that is due but not yet claimed.");
        }
    }

    /// <summary>Total number of rows released for retry across the lifetime of this instance, independent of the counter instrument.</summary>
    internal long RetriedRowCount => Interlocked.Read(ref _retriedRowCount);

    /// <summary>
    /// Records that <paramref name="retriedCount"/> rows of the current batch were released for a
    /// deferred retry, and emits (or suppresses, per the rate limit) the corresponding Warning entry
    /// for the representative row <paramref name="rowId"/> at <paramref name="retryCount"/>.
    /// </summary>
    /// <param name="retriedCount">Number of rows released for retry in this call; must be positive.</param>
    /// <param name="rowId">Id of the representative row (the one with the highest <c>RetryCount</c>).</param>
    /// <param name="retryCount">The representative row's <c>RetryCount</c> after this release.</param>
    internal void RowsReleasedForRetry(int retriedCount, long rowId, int retryCount)
    {
        Interlocked.Add(ref _retriedRowCount, retriedCount);
        _retriedRows?.Add(retriedCount);

        bool shouldLog;
        long suppressedToReport;

        lock (_logLock)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (!_hasLoggedOnce || now - _windowStart >= LogWindow)
            {
                shouldLog = true;
                suppressedToReport = _suppressedCount;
                _suppressedCount = 0;
                _windowStart = now;
                _hasLoggedOnce = true;
            }
            else
            {
                shouldLog = false;
                suppressedToReport = 0;
                _suppressedCount += retriedCount;
            }
        }

        if (shouldLog)
        {
            LogRowsReleasedForRetry(_logger, rowId, retryCount, retriedCount, suppressedToReport);
        }
    }

    /// <summary>
    /// Atomically consumes the gauge's pending sample request, if one is pending. Returns
    /// <see langword="true"/> at most once per call to the gauge's observation callback, and only when
    /// a <see cref="Meter"/> was supplied at construction — without one the flag is never set.
    /// </summary>
    internal bool TryConsumeAgeSampleRequest()
        => Interlocked.CompareExchange(ref _ageSampleRequested, 0, 1) == 1;

    /// <summary>
    /// Records the due instant of the oldest outbox row whose deferred retry is due, or
    /// <see langword="null"/> when none is due. The gauge callback derives the reported age from this
    /// value and the current time at the moment it is observed, not at the moment it was recorded.
    /// </summary>
    internal void RecordOldestDueRetry(DateTimeOffset? oldestDueAt)
        => Volatile.Write(ref _oldestDueUtcTicks, oldestDueAt?.UtcTicks ?? NoRetryDue);

    private IEnumerable<Measurement<double>> ObserveOldestDueRetryAge()
    {
        Interlocked.Exchange(ref _ageSampleRequested, 1);

        long oldestDueUtcTicks = Volatile.Read(ref _oldestDueUtcTicks);
        if (oldestDueUtcTicks == NoSampleYet)
        {
            yield break;
        }

        double age = oldestDueUtcTicks == NoRetryDue
            ? 0.0
            : Math.Max(
                0.0,
                (_timeProvider.GetUtcNow() - new DateTimeOffset(oldestDueUtcTicks, TimeSpan.Zero)).TotalSeconds);

        yield return new Measurement<double>(age);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Outbox row {RowId} was not confirmed by the transport and was released for a deferred retry (RetryCount={RetryCount}); {RetriedCount} row(s) of this batch were released for retry and {SuppressedCount} retried row(s) were not logged since the previous entry.")]
    private static partial void LogRowsReleasedForRetry(
        ILogger logger,
        long rowId,
        int retryCount,
        int retriedCount,
        long suppressedCount);
}
