using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Implements the in-memory transport's native scheduled delivery — the mechanism behind
/// <see cref="InMemoryTransportAdapter"/>'s <see cref="INativeMessageScheduler"/> capability. Validates a
/// scheduled message against the sealed topology, <see cref="InMemoryTransportOptions.MaxMessageSize"/>,
/// the timer due-time limit, and its own pending-message cap — all BEFORE taking ownership of anything —
/// then copies the body into a pooled buffer and arms a <see cref="ITimer"/> that re-sends it through
/// <see cref="InMemorySender"/> once it fires. One instance per <see cref="InMemoryTransportAdapter"/>,
/// created unconditionally (unlike <see cref="InMemoryDeferScheduler"/>, which only exists when
/// <see cref="InMemoryTransportOptions.DeferEnabled"/> is on) since native scheduling is always advertised
/// through <see cref="InMemoryTransportAdapter.Capabilities"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership transfer.</b> Mirrors <see cref="InMemoryDeferScheduler"/>: a successful
/// <see cref="Schedule"/> call transfers ownership of the pooled buffer (and the reservation it holds
/// against <see cref="_maxPending"/>) to this scheduler. From that point on exactly one of three paths
/// retires the entry — the timer callback (fires it, or drops it if this scheduler was disposed in the
/// meantime), <see cref="Cancel"/>, or <see cref="Dispose"/>'s own sweep — via a <c>TryRemove</c> on the
/// same key of the pending-entry map, so whichever of them runs first is the single winner. Every
/// validation that can fail (destination resolution, <see cref="InMemoryTransportOptions.MaxMessageSize"/>,
/// the pending cap, the timer due-time limit) runs BEFORE the buffer is rented and BEFORE the pending-count
/// reservation is taken, so a rejected <see cref="Schedule"/> call never rents a buffer or reserves a slot
/// the caller would have to clean up.
/// </para>
/// <para>
/// <b>No queue slot reserved.</b> Unlike a deferred redelivery (which already holds its queue's reserved
/// slot for the whole delay), a scheduled message reserves nothing on its destination queue while it is
/// pending — only an entry in this scheduler's own, separately-capped pending map. Once the timer fires,
/// the copy goes through the ordinary <see cref="InMemorySender.SendAsync"/> path and is subject to the
/// same queue capacity, <see cref="InMemoryTransportOptions.SendTimeout"/>, and rejection behavior as any
/// other send — reserving a queue slot for up to <see cref="InMemoryTransportOptions.MaxDeferDelay"/> would
/// otherwise starve the queue for everything else.
/// </para>
/// <para>
/// <b>No retry.</b> A fired copy that is not confirmed (queue full after <see cref="InMemoryTransportOptions.SendTimeout"/>,
/// or the transport closed) is never retried — the same at-most-once semantics as <see cref="InMemorySender.SendAsync"/>
/// itself. The failure is counted on <see cref="DeliveryFailedCount"/> and logged, aggregated and throttled
/// per validated exchange (never the publisher-supplied routing key, which has unbounded cardinality — see
/// <see cref="ReportDeliveryFailed"/>), never once per message.
/// </para>
/// </remarks>
internal sealed partial class InMemoryMessageScheduler : IDisposable
{
    /// <summary>
    /// The default cap on the number of scheduled messages this scheduler holds pending at once. Bounds
    /// the worst-case memory footprint of scheduled-but-undelivered message bodies — see the package
    /// README's "Memory bound" section.
    /// </summary>
    internal const int DefaultMaxPending = 10_000;

    private static readonly TimeSpan DeliveryFailedLogWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DroppedOnShutdownLogWindow = TimeSpan.FromSeconds(60);

    // The AMQP binding-key length limit (InMemoryRouter.MaxRoutingKeyBytes) — reused only as a
    // conservative truncation point for the routing key and exchange name echoed into an exception
    // message (see SanitizeForMessage), not to enforce any length limit itself (InMemoryRouter.Route
    // already rejects an over-long key on its own).
    private const int MaxRoutingKeyCharsInMessage = InMemoryRouter.MaxRoutingKeyBytes;

    private readonly TimeProvider _timeProvider;
    private readonly InMemoryBufferPool _pool;
    private readonly InMemoryRouter _router;
    private readonly InMemorySender _sender;
    private readonly InMemoryTransportOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationToken _shutdownToken;
    private readonly int _maxPending;

    private readonly ConcurrentDictionary<long, Pending> _pending = new();

    // Keyed by the exchange validated at schedule time — never the publisher-supplied routing key, which
    // has unbounded cardinality (see InMemorySendDiagnostics's own "a publisher-supplied name never
    // becomes a throttle key" rule). The key set is therefore bounded by the sealed topology's declared
    // exchange count plus the default exchange, built once in the constructor.
    private readonly FrozenDictionary<string, ThrottleState> _deliveryFailedThrottle;
    private readonly FrozenDictionary<string, ThrottleState> _droppedOnShutdownThrottle;

    private long _nextId;
    private int _pendingCount;
    private long _deliveryFailedCount;
    private long _logFailureCount;
    private int _disposed;

    /// <param name="timeProvider">The time source scheduled-delivery timers are created from.</param>
    /// <param name="pool">The pool a scheduled message's buffer is rented from and returned to.</param>
    /// <param name="router">Resolves a scheduled message's destination against the sealed topology.</param>
    /// <param name="sender">Re-sends a fired scheduled message through the ordinary send path.</param>
    /// <param name="options">The transport options a scheduled message is validated against.</param>
    /// <param name="logger">The logger scheduled-delivery diagnostics are reported to.</param>
    /// <param name="shutdownToken">
    /// The owning adapter's shutdown token, forwarded unchanged to <see cref="InMemorySender.SendAsync"/>
    /// on the fire path.
    /// </param>
    /// <param name="maxPending">
    /// The cap on the number of scheduled messages held pending at once. Defaults to
    /// <see cref="DefaultMaxPending"/>; a non-positive value also falls back to it.
    /// </param>
    internal InMemoryMessageScheduler(
        TimeProvider timeProvider,
        InMemoryBufferPool pool,
        InMemoryRouter router,
        InMemorySender sender,
        InMemoryTransportOptions options,
        ILogger logger,
        CancellationToken shutdownToken,
        int maxPending = DefaultMaxPending)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _timeProvider = timeProvider;
        _pool = pool;
        _router = router;
        _sender = sender;
        _options = options;
        _logger = logger;
        _shutdownToken = shutdownToken;
        _maxPending = maxPending > 0 ? maxPending : DefaultMaxPending;

        _deliveryFailedThrottle = BuildThrottleTable(router.Registry);
        _droppedOnShutdownThrottle = BuildThrottleTable(router.Registry);
    }

    /// <summary>Gets the number of scheduled messages currently pending. A test hook.</summary>
    internal int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>
    /// Gets the number of fired scheduled messages whose delivery was not confirmed (queue full, or the
    /// transport closed while the copy was in flight) — never retried. A test hook.
    /// </summary>
    internal long DeliveryFailedCount => Interlocked.Read(ref _deliveryFailedCount);

    /// <summary>
    /// Gets the number of metric or logger calls from this instance that threw and were suppressed — see
    /// the explicit catch-and-count guard on every logging call below. A throwing logging provider must
    /// never crash the timer thread <see cref="TimerCallback"/> runs on, never fault the fire-and-forget
    /// task <see cref="FireAsync"/> runs as, and never abort <see cref="Dispose"/> before its own sweep
    /// completes. A test hook.
    /// </summary>
    internal long LogFailureCount => Interlocked.Read(ref _logFailureCount);

    /// <summary>
    /// Validates and schedules <paramref name="message"/> for delivery at <paramref name="scheduledEnqueueTime"/>.
    /// </summary>
    /// <remarks>
    /// Validation order — every check below runs before this call takes ownership of anything (rents a
    /// buffer, reserves a pending-count slot, or arms a timer): (1) <paramref name="message"/> not
    /// <see langword="null"/>, this scheduler not disposed, <paramref name="cancellationToken"/> not
    /// already cancelled; (2) the computed delay clamped to <see cref="TimeSpan.Zero"/> when negative,
    /// rejected when it exceeds <see cref="InMemoryTransportOptions.MaxDeferDelay"/> (the
    /// <see cref="ITimer"/> due-time limit); (3) the destination — the <c>BW-Exchange</c> header
    /// (<see cref="InMemorySender.TryGetExchangeHeader"/>) or <see cref="ExchangeRegistry.DefaultExchangeName"/>,
    /// routed via <see cref="InMemoryRouter.Route"/> against the sealed topology — must resolve to at
    /// least one queue; (4) the body must not exceed <see cref="InMemoryTransportOptions.MaxMessageSize"/>;
    /// (5) <see cref="PendingCount"/> must be below the configured cap. Only once every check has passed
    /// is a buffer rented from <paramref name="message"/>'s body, the publisher's headers snapshotted
    /// (every case-variant of <c>BW-Exchange</c> replaced with the exchange actually validated above), and
    /// a timer armed.
    /// </remarks>
    /// <param name="message">The message to schedule. Its body is copied; the caller keeps ownership of it.</param>
    /// <param name="scheduledEnqueueTime">The UTC time at which the message should be delivered.</param>
    /// <param name="cancellationToken">Checked once, before any work begins.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This scheduler has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The delay until <paramref name="scheduledEnqueueTime"/> exceeds <see cref="InMemoryTransportOptions.MaxDeferDelay"/>.
    /// </exception>
    /// <exception cref="BareWireTransportException">
    /// The destination does not resolve to any queue in the sealed topology, the body exceeds
    /// <see cref="InMemoryTransportOptions.MaxMessageSize"/>, or the pending-message cap is reached.
    /// </exception>
    internal ScheduledMessageToken Schedule(
        OutboundMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        TimeSpan delay = scheduledEnqueueTime - _timeProvider.GetUtcNow();
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        if (delay > InMemoryTransportOptions.MaxDeferDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scheduledEnqueueTime), scheduledEnqueueTime,
                $"The delay until the scheduled enqueue time must not exceed {InMemoryTransportOptions.MaxDeferDelay} " +
                "(the in-memory timer's due-time limit, about 49.7 days).");
        }

        InMemorySender.TryGetExchangeHeader(message.Headers, out string? exchangeHeader);
        string exchange = exchangeHeader ?? ExchangeRegistry.DefaultExchangeName;

        InMemoryRouteResult route = _router.Route(exchange, message.RoutingKey);
        if (!route.IsRouted)
        {
            throw new BareWireTransportException(
                $"The scheduled message destination '{SanitizeForMessage(exchange)}'/'{SanitizeForMessage(message.RoutingKey)}' " +
                "does not resolve to any queue in the sealed in-memory topology. Scheduled delivery never deploys " +
                "topology at runtime.",
                "InMemory", null);
        }

        if (message.Body.Length > _options.MaxMessageSize)
        {
            throw new BareWireTransportException(
                $"The scheduled message body ({message.Body.Length} bytes) exceeds MaxMessageSize " +
                $"({_options.MaxMessageSize} bytes).",
                "InMemory", null);
        }

        int reservedCount = Interlocked.Increment(ref _pendingCount);
        if (reservedCount > _maxPending)
        {
            Interlocked.Decrement(ref _pendingCount);
            throw new BareWireTransportException(
                $"The in-memory transport already has {_maxPending} scheduled message(s) pending; no more can be " +
                "scheduled until an earlier one is delivered, cancelled, or the transport is disposed.",
                "InMemory", null);
        }

        bool committed = false;
        byte[] buffer = _pool.Rent(message.Body.Length);
        try
        {
            message.Body.Span.CopyTo(buffer);
            Dictionary<string, string> headers = SnapshotHeaders(message.Headers, exchange);

            long id = Interlocked.Increment(ref _nextId);
            var pending = new Pending(message.RoutingKey, exchange, headers, buffer, message.Body.Length, message.ContentType);

            // Infinite due time first — the entry is published to the pending map only afterwards, so the
            // callback can never fire before the entry it looks up actually exists. See
            // InMemoryDeferScheduler's remarks on timer ordering for the full rationale.
            //
            // ExecutionContext.SuppressFlow() throws InvalidOperationException when flow is already
            // suppressed on this thread (for example a caller running inside its own SuppressFlow scope,
            // or a host that suppresses flow globally) — guarded so Schedule never throws for that reason
            // alone; the timer is still created either way, just without this method redundantly
            // suppressing flow that is already suppressed.
            ITimer timer = ExecutionContext.IsFlowSuppressed()
                ? _timeProvider.CreateTimer(TimerCallback, id, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
                : CreateTimerWithSuppressedFlow(id);

            pending.Timer = timer;
            _pending[id] = pending;
            committed = true; // ownership (the buffer and the pending-count reservation) now belongs to this entry

            if (Volatile.Read(ref _disposed) != 0)
            {
                // Lost the race with a concurrent Dispose() that already swept the map before this entry
                // existed: nobody else will ever clean this entry up, so this call must, right here.
                if (_pending.TryRemove(id, out _))
                {
                    timer.Dispose();
                    Interlocked.Decrement(ref _pendingCount);
                    _pool.Return(buffer);
                }

                throw new ObjectDisposedException(nameof(InMemoryMessageScheduler));
            }

            try
            {
                timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
            catch
            {
                // Arming failed after the entry was published. Ownership already moved to this
                // scheduler, so retire the entry here (unless Dispose()'s sweep claimed it first) before
                // the exception propagates.
                if (_pending.TryRemove(id, out _))
                {
                    timer.Dispose();
                    Interlocked.Decrement(ref _pendingCount);
                    _pool.Return(buffer);
                }

                throw;
            }

            return new ScheduledMessageToken(id, message.RoutingKey);
        }
        finally
        {
            if (!committed)
            {
                _pool.Return(buffer);
                Interlocked.Decrement(ref _pendingCount);
            }
        }
    }

    /// <summary>
    /// Cancels the scheduled message identified by <paramref name="token"/>. Idempotent and best-effort: a
    /// no-op when <paramref name="token"/> names an unknown id, an id whose message already fired or was
    /// already cancelled, or an id whose stored destination does not match <paramref name="token"/>'s own
    /// (a forged or stale token can never cancel a different scheduled message than the one it was issued
    /// for). A successful cancellation disposes the entry's timer and returns its buffer to the pool
    /// exactly once.
    /// </summary>
    internal void Cancel(ScheduledMessageToken token)
    {
        if (!_pending.TryGetValue(token.SequenceNumber, out Pending? pending) ||
            !string.Equals(pending.Destination, token.Destination, StringComparison.Ordinal))
        {
            return;
        }

        if (!_pending.TryRemove(token.SequenceNumber, out Pending? removed))
        {
            // Lost the race with the timer callback or Dispose()'s sweep between the checks above.
            return;
        }

        removed.Timer!.Dispose();
        Interlocked.Decrement(ref _pendingCount);
        _pool.Return(removed.Buffer);
    }

    /// <summary>
    /// Creates this entry's timer with <see cref="ExecutionContext"/> flow suppressed, so its callback
    /// never captures (and later replays) the caller's ambient <see cref="ExecutionContext"/> — the same
    /// isolation <see cref="InMemoryDeferScheduler"/> applies to its own timers. Only called when flow is
    /// not already suppressed; see the guard at the call site.
    /// </summary>
    private ITimer CreateTimerWithSuppressedFlow(long id)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return _timeProvider.CreateTimer(TimerCallback, id, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Claims the pending entry (a <c>TryRemove</c> on the same key <see cref="Cancel"/> and
    /// <see cref="Dispose"/> use) and, when this scheduler has not been disposed and its shutdown token
    /// has not been cancelled in the meantime, fires it through <see cref="FireAsync"/>. Drops it instead
    /// — returning its buffer, releasing its pending-count reservation, and reporting an aggregated,
    /// throttled shutdown-drop log — when either condition is true (the common case, every pending entry,
    /// is instead handled synchronously and reported in one aggregated call by <see cref="Dispose"/>
    /// itself; this path only ever fires for the narrow race where a timer's due time is reached exactly
    /// as that sweep runs, or between <c>InMemoryTransportAdapter.Dispose</c> cancelling its shutdown token
    /// and it reaching this scheduler's own <see cref="Dispose"/> call). Runs on a timer thread with
    /// nothing observing its return value or an exception from it — no exception is ever allowed to
    /// escape this method, or it would crash that thread.
    /// </summary>
    private void TimerCallback(object? state)
    {
        try
        {
            TimerCallbackCore(state);
        }
        catch (Exception)
        {
            // Defense in depth on top of the guarded log calls below: nothing here is expected to throw
            // once every log call is guarded, but a timer callback has no caller to observe an escaping
            // exception, and letting one through would crash the process.
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    private void TimerCallbackCore(object? state)
    {
        long id = (long)state!;
        if (!_pending.TryRemove(id, out Pending? pending))
        {
            return;
        }

        pending.Timer!.Dispose();

        if (Volatile.Read(ref _disposed) != 0 || _shutdownToken.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _pendingCount);
            _pool.Return(pending.Buffer);
            ReportDroppedOnShutdown(pending.Exchange, 1);
            return;
        }

        // The pending-count reservation is released by FireAsync's own finally block, not here, so the
        // cap this scheduler enforces (see Schedule) also covers a fire still in flight, not just an
        // entry sitting in the pending map.
        _ = FireAsync(pending);
    }

    /// <summary>
    /// Re-sends <paramref name="pending"/>'s copy through <see cref="InMemorySender.SendAsync"/>, releases
    /// its pending-count reservation, and returns its buffer to the pool exactly once, no matter which of
    /// the paths below is taken. Never throws and never faults its own returned <see cref="Task"/>: every
    /// exception is caught, logged (aggregated and throttled per validated exchange — never the message
    /// body, header values, or the publisher-supplied routing key), and counted, so this method is always
    /// safe to run as an unobserved, fire-and-forget task from <see cref="TimerCallback"/>.
    /// </summary>
    private async Task FireAsync(Pending pending)
    {
        bool droppedOnShutdown = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _shutdownToken.IsCancellationRequested)
            {
                // Races adapter shutdown between TimerCallback handing this entry off and this method
                // actually running: classified as a shutdown drop, never a delivery failure. The send is
                // skipped entirely rather than racing InMemorySender against a transport that is already
                // tearing down.
                droppedOnShutdown = true;
                return;
            }

            var outbound = new OutboundMessage(
                pending.Destination, pending.Headers, new ReadOnlyMemory<byte>(pending.Buffer, 0, pending.Length),
                pending.ContentType);

            IReadOnlyList<SendResult> results = await _sender.SendAsync([outbound], _shutdownToken).ConfigureAwait(false);

            if (!results[0].IsConfirmed)
            {
                if (Volatile.Read(ref _disposed) != 0 || _shutdownToken.IsCancellationRequested)
                {
                    droppedOnShutdown = true;
                }
                else
                {
                    Interlocked.Increment(ref _deliveryFailedCount);
                    ReportDeliveryFailed(pending.Exchange);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The transport closed while this fire was in flight — a shutdown drop (D7), not a delivery
            // failure: no retry policy would apply to either outcome, but the two are counted separately.
            droppedOnShutdown = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _deliveryFailedCount);
            try
            {
                LogScheduledDeliveryException(_logger, pending.Exchange, ex);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _logFailureCount);
            }
        }
        finally
        {
            // Released here, not in TimerCallback, so the pending-count cap covers a fire still in
            // flight — see TimerCallbackCore's own remark.
            Interlocked.Decrement(ref _pendingCount);
            _pool.Return(pending.Buffer);
            if (droppedOnShutdown)
            {
                ReportDroppedOnShutdown(pending.Exchange, 1);
            }
        }
    }

    /// <summary>
    /// Claims every entry still pending, disposes its timer, returns its buffer, and reports one
    /// aggregated, throttled shutdown-drop log per validated exchange. Idempotent. Never throws — see
    /// <see cref="LogFailureCount"/> — so <c>InMemoryTransportAdapter.Dispose</c> can always run its own
    /// <c>InFlight</c> sweep afterwards, even when a logging provider misbehaves.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            DisposeCore();
        }
        catch (Exception)
        {
            // Defense in depth, mirroring TimerCallback's own guard: nothing here is expected to throw
            // once every log call below is guarded, but a throwing ITimer.Dispose() or a similarly
            // misbehaving collaborator must never abort the adapter's own shutdown sequence, which still
            // has its InFlight sweep to run after this call returns.
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    private void DisposeCore()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var droppedPerExchange = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<long, Pending> entry in _pending)
        {
            if (!_pending.TryRemove(entry.Key, out Pending? pending))
            {
                // Already claimed by the timer callback, or by a concurrent Schedule's own recheck.
                continue;
            }

            pending.Timer!.Dispose();
            Interlocked.Decrement(ref _pendingCount);
            _pool.Return(pending.Buffer);
            droppedPerExchange[pending.Exchange] = droppedPerExchange.GetValueOrDefault(pending.Exchange) + 1;
        }

        foreach (KeyValuePair<string, int> dropped in droppedPerExchange)
        {
            ReportDroppedOnShutdown(dropped.Key, dropped.Value);
        }
    }

    /// <summary>
    /// Builds the header snapshot a pending entry keeps: every publisher-supplied header except any
    /// case-variant of <c>BW-Exchange</c>, plus <c>BW-Exchange</c> set explicitly to <paramref name="exchange"/>
    /// — the exchange actually validated by <see cref="Schedule"/> — so a differently-cased header the
    /// publisher supplied (for example <c>bw-exchange</c>) can never re-route the fired copy to a
    /// different exchange than the one resolved at schedule time.
    /// </summary>
    private static Dictionary<string, string> SnapshotHeaders(IReadOnlyDictionary<string, string> source, string exchange)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in source)
        {
            if (string.Equals(entry.Key, InMemoryHeaderNames.Exchange, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            snapshot[entry.Key] = entry.Value;
        }

        snapshot[InMemoryHeaderNames.Exchange] = exchange;
        return snapshot;
    }

    /// <summary>
    /// Sanitizes <paramref name="value"/> (a publisher-supplied exchange name or routing key) for inclusion
    /// in an exception message: replaces every control character (including CR/LF, which could otherwise
    /// forge extra lines in a sink that renders the exception message as plain text) with a space, then
    /// truncates the result to <see cref="MaxRoutingKeyCharsInMessage"/> characters.
    /// </summary>
    private static string SanitizeForMessage(string value) => Truncate(EscapeControlCharacters(value));

    private static string Truncate(string value) =>
        value.Length > MaxRoutingKeyCharsInMessage
            ? $"{value[..MaxRoutingKeyCharsInMessage]}...(truncated, {value.Length} chars total)"
            : value;

    private static string EscapeControlCharacters(string value)
    {
        int firstControl = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                firstControl = i;
                break;
            }
        }

        if (firstControl < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, firstControl);
        for (int i = firstControl; i < value.Length; i++)
        {
            char c = value[i];
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Records that a fired scheduled message's delivery to <paramref name="exchange"/> was not confirmed.
    /// Throttled and aggregated per <paramref name="exchange"/> — never per the publisher-supplied routing
    /// key, whose cardinality is unbounded. <paramref name="exchange"/> is always a value validated by
    /// <see cref="Schedule"/> (the default exchange or a declared one), so it is always a key of this
    /// scheduler's own throttle table, built by <see cref="BuildThrottleTable"/>; the fallback below is
    /// defensive only.
    /// </summary>
    private void ReportDeliveryFailed(string exchange)
    {
        if (!_deliveryFailedThrottle.TryGetValue(exchange, out ThrottleState? state))
        {
            _deliveryFailedThrottle.TryGetValue(ExchangeRegistry.DefaultExchangeName, out state);
        }

        if (state is null || !state.TryEnterLogWindow(_timeProvider, DeliveryFailedLogWindow, out int suppressedCount))
        {
            return;
        }

        try
        {
            LogScheduledDeliveryFailed(_logger, exchange, suppressedCount);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    /// <summary>
    /// Records that <paramref name="count"/> scheduled message(s) destined for <paramref name="exchange"/>
    /// were dropped because the transport shut down. Throttled and aggregated per
    /// <paramref name="exchange"/> for the same reason as <see cref="ReportDeliveryFailed"/>.
    /// </summary>
    private void ReportDroppedOnShutdown(string exchange, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (!_droppedOnShutdownThrottle.TryGetValue(exchange, out ThrottleState? state))
        {
            _droppedOnShutdownThrottle.TryGetValue(ExchangeRegistry.DefaultExchangeName, out state);
        }

        if (state is null || !state.TryEnterLogWindow(_timeProvider, DroppedOnShutdownLogWindow, out int suppressedCount))
        {
            return;
        }

        try
        {
            LogDroppedOnShutdown(_logger, count, exchange, suppressedCount);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }
    }

    /// <summary>
    /// Builds the fixed, frozen throttle-state table this scheduler's two throttle maps are seeded with:
    /// one entry per declared exchange in <paramref name="registry"/>, plus the default exchange — the
    /// bounded set of scopes <see cref="ReportDeliveryFailed"/> and <see cref="ReportDroppedOnShutdown"/>
    /// are ever called with, since both are always called with the exchange <see cref="Schedule"/> already
    /// validated. Mirrors <c>InMemorySendDiagnostics.BuildThrottleTable</c>.
    /// </summary>
    private static FrozenDictionary<string, ThrottleState> BuildThrottleTable(ExchangeRegistry registry)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal) { ExchangeRegistry.DefaultExchangeName };
        foreach (string exchangeName in registry.Exchanges.Keys)
        {
            scopes.Add(exchangeName);
        }

        var table = new Dictionary<string, ThrottleState>(scopes.Count, StringComparer.Ordinal);
        foreach (string scope in scopes)
        {
            table[scope] = new ThrottleState();
        }

        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Scheduled in-memory message delivery via exchange '{Exchange}' was not confirmed (the target " +
        "queue was full, or the transport shut down while the copy was in flight). Scheduled delivery is " +
        "never retried. {SuppressedCount} earlier occurrence(s) for this exchange were suppressed since " +
        "the last warning.")]
    private static partial void LogScheduledDeliveryFailed(ILogger logger, string exchange, int suppressedCount);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Scheduled in-memory message delivery via exchange '{Exchange}' failed with an unexpected " +
        "exception. Scheduled delivery is never retried.")]
    private static partial void LogScheduledDeliveryException(ILogger logger, string exchange, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "In-memory transport shut down with {Count} scheduled message(s) still pending for exchange " +
        "'{Exchange}'; they were dropped and will never be delivered. Scheduled delivery is not durable " +
        "across shutdown. {SuppressedCount} earlier occurrence(s) for this exchange were suppressed " +
        "since the last log entry.")]
    private static partial void LogDroppedOnShutdown(ILogger logger, int count, string exchange, int suppressedCount);

    /// <summary>One scheduled message awaiting its due time.</summary>
    private sealed class Pending(
        string destination, string exchange, Dictionary<string, string> headers, byte[] buffer, int length, string contentType)
    {
        /// <summary>
        /// The destination this entry fires to — <see cref="OutboundMessage.RoutingKey"/> of the message
        /// that was scheduled, and the same value carried as <see cref="ScheduledMessageToken.Destination"/>.
        /// Publisher-controlled and of unbounded cardinality — never used as a log-throttle key or a
        /// metric tag; see <see cref="Exchange"/> for the bounded scope used for that.
        /// </summary>
        internal string Destination { get; } = destination;

        /// <summary>
        /// The exchange <see cref="Schedule"/> validated this entry's destination against — the default
        /// exchange or a declared one, always a bounded, finite value. Used exclusively as the log-throttle
        /// key for <see cref="ReportDeliveryFailed"/> and <see cref="ReportDroppedOnShutdown"/>; never the
        /// publisher-supplied <see cref="Destination"/>.
        /// </summary>
        internal string Exchange { get; } = exchange;

        /// <summary>The snapshotted header set, with <c>BW-Exchange</c> stamped to the validated exchange.</summary>
        internal Dictionary<string, string> Headers { get; } = headers;

        /// <summary>The pooled buffer holding this entry's copy of the message body.</summary>
        internal byte[] Buffer { get; } = buffer;

        /// <summary>The number of bytes of <see cref="Buffer"/> that belong to the message body.</summary>
        internal int Length { get; } = length;

        internal string ContentType { get; } = contentType;

        internal ITimer? Timer { get; set; }
    }

    /// <summary>
    /// Per-exchange log throttle state: logs at most once per configured window, reporting how many
    /// occurrences were suppressed since the previous log entry. Mirrors <c>InMemoryRouter</c>'s and
    /// <c>InMemoryConsumeDiagnostics</c>'s own per-scope throttle states.
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
