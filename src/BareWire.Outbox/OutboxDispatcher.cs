using System.Buffers;
using System.Collections.Frozen;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox;

/// <summary>
/// Background service that claims pending outbox rows, sends them through the transport and records
/// the outcome: confirmed rows are marked delivered, rejected rows are released for a deferred retry.
/// </summary>
/// <remarks>
/// Delivery is at-least-once. With per-key ordering, confirmed siblings queued behind a rejected head
/// of their key are held back by the ordering barrier: they are released without being marked
/// delivered, so they are sent again on a later cycle even though the broker already accepted them.
/// Consumers that must not observe such duplicates need inbox deduplication.
/// </remarks>
internal sealed partial class OutboxDispatcher : IHostedService, IAsyncDisposable
{
    // Maximum number of back-to-back full batches the polling loop drains before forcing a
    // PollingInterval pause. Caps the catch-up rate at MaxConsecutiveDrains × DispatchBatchSize per
    // PollingInterval, so a large backlog cannot turn the drain into an unbounded tight send loop that
    // churns one broker channel (and one DB row-claim UPDATE) per batch as fast as the process can spin.
    // It still raises the single-instance ceiling far above the ~DispatchBatchSize-per-PollingInterval of
    // a pure timer (10× at the defaults), just not without limit. Internal, not configurable by design.
    internal const int MaxConsecutiveDrains = 10;

    // Streak cap once the current drain streak has seen at least one transport nack. A deliberate
    // backpressure trade-off: nacked rows are already deferred per row by the store, so draining cannot
    // hot-retry them, but a struggling broker still gets half the catch-up burst of a healthy one.
    internal const int MaxConsecutiveDrainsWithNacks = MaxConsecutiveDrains / 2;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITransportAdapter _adapter;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private CancellationTokenSource? _cts;
    private CancellationTokenRegistration _startedRegistration;
    private Task? _pollingTask;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        ITransportAdapter adapter,
        OutboxOptions options,
        ILogger<OutboxDispatcher> logger,
        IHostApplicationLifetime lifetime)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _cts.Token;

        // Defer the polling loop until the host has FULLY started. IHostApplicationLifetime.ApplicationStarted
        // fires only after every IHostedService.StartAsync has completed successfully — and never fires if
        // startup aborts — so the dispatcher claims/sends nothing from a process that never became healthy
        // (publishing a message and marking the row delivered are irreversible external side effects). The
        // callback runs on the host's startup thread, so it only kicks the loop onto the thread pool
        // (Task.Run), never runs it inline. If the token is already signalled (the host has already started)
        // Register invokes the callback synchronously, which still merely schedules the loop and returns.
        _startedRegistration = _lifetime.ApplicationStarted.Register(
            () => _pollingTask = Task.Run(() => RunPollingLoopAsync(token), token));

        LogDispatcherStarted(_logger, _options.PollingInterval, _options.DispatchBatchSize);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        LogDispatcherStopping(_logger);

        // Dispose the ApplicationStarted registration FIRST so the loop cannot start after stop begins.
        // CancellationTokenRegistration.Dispose() blocks until any in-flight callback completes, so once
        // it returns _pollingTask is either set (callback ran) or will never be set (callback removed).
        _startedRegistration.Dispose();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on graceful shutdown — swallow.
            }
        }

        LogDispatcherStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        _startedRegistration.Dispose();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    // Drain rule: claim the next batch immediately (no PollingInterval pause) only when the batch came
    // back full (more backlog is likely), at least half of it was confirmed (2 × Confirmed ≥ Claimed),
    // and the drain streak is still under its cap — halved once the streak has seen a transport nack.
    // The product is computed in long so it cannot overflow.
    internal static bool ShouldDrainImmediately(
        int claimed,
        int confirmed,
        int batchSize,
        int consecutiveDrains,
        bool streakHasNacks)
    {
        int streakCap = streakHasNacks ? MaxConsecutiveDrainsWithNacks : MaxConsecutiveDrains;

        return claimed >= batchSize
            && 2L * confirmed >= claimed
            && consecutiveDrains < streakCap;
    }

    private async Task RunPollingLoopAsync(CancellationToken ct)
    {
        // Relative-paced poll loop. Each iteration dispatches one batch, then decides how long to wait
        // before the next claim based on the OUTCOME — pacing is relative to this batch's completion,
        // never to a fixed timer grid:
        //   - FULL batch with at least half of it confirmed, streak under its cap → claim again
        //     immediately (drain the backlog), so a single instance is not capped at
        //     ~DispatchBatchSize per PollingInterval (see ShouldDrainImmediately).
        //   - anything else (empty, partial, majority nacked, a transient error, or the streak cap
        //     reached) → wait one PollingInterval before the next claim.
        // Draining despite a minority of nacks cannot hot-retry the rejected rows: the store defers each
        // nacked row (not claimable again before at least one PollingInterval), so an immediate re-claim
        // only picks up fresh backlog. The relative delay still paces the loop itself: a pause always
        // lasts ~PollingInterval from the end of the batch, regardless of how long a preceding drain ran.
        // Counts consecutive immediate drains so a sustained backlog drains in bounded bursts rather than
        // an unbounded tight loop; the cap is halved once the current streak has seen a transport nack.
        // The streak state is reset whenever the loop pauses.
        int consecutiveDrains = 0;
        bool streakHasNacks = false;
        int streakBatches = 0;
        int streakClaimed = 0;
        int streakNacked = 0;

        while (!ct.IsCancellationRequested)
        {
            (int Claimed, int Confirmed, int Nacked) batch;
            try
            {
                batch = await DispatchBatchAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDispatchError(_logger, ex);
                // Transient error: treat as no progress so the loop paces before retrying (below).
                batch = default;
            }

            streakBatches++;
            streakClaimed += batch.Claimed;
            streakNacked += batch.Nacked;
            streakHasNacks |= batch.Nacked > 0;

            if (ShouldDrainImmediately(
                    batch.Claimed,
                    batch.Confirmed,
                    _options.DispatchBatchSize,
                    consecutiveDrains,
                    streakHasNacks))
            {
                consecutiveDrains++;
                continue;
            }

            if (streakNacked > 0)
            {
                LogDrainStreakNackShare(_logger, streakBatches, streakNacked, streakClaimed);
            }

            consecutiveDrains = 0;
            streakHasNacks = false;
            streakBatches = 0;
            streakClaimed = 0;
            streakNacked = 0;
            await Task.Delay(_options.PollingInterval, ct).ConfigureAwait(false);
        }
    }

    // Returns (Claimed, Confirmed, Nacked) for one cycle:
    //   - Claimed: rows claimed from the store.
    //   - Confirmed: rows marked delivered. Confirmed siblings held back by the per-key ordering barrier
    //     are NOT counted — they are released, not delivered, so they are no forward progress.
    //   - Nacked: rows the transport rejected (counted from the send results only).
    // The polling loop uses these to decide between an immediate drain and a PollingInterval pause.
    private async Task<(int Claimed, int Confirmed, int Nacked)> DispatchBatchAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IOutboxStore store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        IReadOnlyList<OutboxEntry> pending = await store
            .GetPendingAsync(_options.DispatchBatchSize, ct)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return (0, 0, 0);
        }

        LogDispatching(_logger, pending.Count);

        OutboundMessage[] messages = new OutboundMessage[pending.Count];
        long[] ids = new long[pending.Count];

        for (int i = 0; i < pending.Count; i++)
        {
            OutboxEntry entry = pending[i];
            ids[i] = entry.Id;
            messages[i] = new OutboundMessage(
                routingKey: entry.RoutingKey,
                headers: entry.Headers,
                body: entry.PooledBody.AsMemory(0, entry.BodyLength),
                contentType: entry.ContentType);
        }

        // Ids whose pooled buffer the store retained ownership of when releasing the lock (e.g.
        // re-enqueued in-memory entries). Their buffers must NOT be returned to the ArrayPool below
        // — the store still references them. Empty for EF Core (fresh per-cycle buffers).
        IReadOnlySet<long> retainedByStore = FrozenSet<long>.Empty;
        int confirmedCount = 0;
        int nackedCount = 0;

        try
        {
            IReadOnlyList<SendResult> results = await _adapter.SendBatchAsync(messages, ct).ConfigureAwait(false);

            // Only mark entries as delivered if the broker confirmed them.
            List<long> confirmedIds = new(pending.Count);
            List<long> nackedIds = [];

            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].IsConfirmed)
                {
                    confirmedIds.Add(ids[i]);
                }
                else
                {
                    nackedIds.Add(ids[i]);
                }
            }

            nackedCount = nackedIds.Count;

            // Rows the transport accepted but that are held back only by the per-key ordering barrier.
            // Kept apart from nackedIds: the store releases them without a deferral and without counting
            // a retry. Empty (no allocation) unless the per-key barrier applies.
            IReadOnlyList<long> barrierReleasedIds = Array.Empty<long>();

            // Per-key barrier: when PerKey ordering is active and at least one entry was nacked, apply
            // head-of-line blocking per key. Any confirmed sibling with a higher Id than the first nacked
            // entry in its key group must not be marked delivered until the nacked head is retried and
            // confirmed. Keyless entries (OrderingKey == null) are always independent and never blocked.
            //
            // The None path takes no extra branches and allocates no Dictionary — zero overhead on the
            // default ordering mode.
            if (_options.OrderingMode == OrderingMode.PerKey && nackedIds.Count > 0)
            {
                // Rebuild the three lists applying per-key head-of-line blocking. Keyless entries
                // (OrderingKey == null) are handled in a separate pre-pass so that the keyed dictionary
                // uses non-nullable string keys (satisfying the notnull TKey constraint).
                confirmedIds = new List<long>(pending.Count);
                nackedIds = [];
                List<long> blockedSiblingIds = [];

                // Pre-pass: keyless entries are always independent — route them directly.
                for (int i = 0; i < pending.Count; i++)
                {
                    if (pending[i].OrderingKey is null)
                    {
                        if (results[i].IsConfirmed) confirmedIds.Add(ids[i]);
                        else nackedIds.Add(ids[i]);
                    }
                }

                // Group keyed entries by OrderingKey, tracking per-entry confirmation outcome.
                var groups = new Dictionary<string, List<(long Id, bool Confirmed)>>(StringComparer.Ordinal);

                for (int i = 0; i < pending.Count; i++)
                {
                    string? key = pending[i].OrderingKey;
                    if (key is null) continue; // already handled above

                    if (!groups.TryGetValue(key, out List<(long, bool)>? list))
                    {
                        list = [];
                        groups[key] = list;
                    }

                    list.Add((ids[i], results[i].IsConfirmed));
                }

                foreach (List<(long Id, bool Confirmed)> group in groups.Values)
                {
                    // Find the minimum Id among nacked entries in this key group.
                    // By definition all entries with Id < firstNackedId are confirmed — if any
                    // were nacked they would themselves be the minimum.
                    long firstNackedId = long.MaxValue;
                    foreach ((long id, bool confirmed) in group)
                    {
                        if (!confirmed && id < firstNackedId) firstNackedId = id;
                    }

                    foreach ((long id, bool confirmed) in group)
                    {
                        if (id < firstNackedId)
                        {
                            // Before the barrier (or no nack in this group): delivered.
                            confirmedIds.Add(id);
                        }
                        else if (!confirmed)
                        {
                            // Rejected by the transport: a nack (deferred retry).
                            nackedIds.Add(id);
                        }
                        else
                        {
                            // Accepted by the transport but behind the nacked head: barrier sibling.
                            blockedSiblingIds.Add(id);
                        }
                    }
                }

                barrierReleasedIds = blockedSiblingIds;

                if (blockedSiblingIds.Count > 0)
                {
                    LogPerKeyBarrierApplied(_logger, blockedSiblingIds.Count);
                }
            }

            confirmedCount = confirmedIds.Count;

            if (confirmedIds.Count > 0)
            {
                await store.MarkDeliveredAsync(confirmedIds, ct).ConfigureAwait(false);
            }

            if (nackedIds.Count > 0 || barrierReleasedIds.Count > 0)
            {
                // Release this instance's claim on both kinds of rows in ONE store call, so neither waits
                // for OutboxLockTimeout: nacked rows are deferred by the store's backoff schedule (not
                // claimable again before their deferral elapses, RetryCount incremented); barrier siblings
                // are released without a deferral and without counting a retry.
                retainedByStore = await store
                    .ReleaseLockAsync(nackedIds, barrierReleasedIds, ct)
                    .ConfigureAwait(false);
            }

            if (nackedIds.Count > 0)
            {
                LogPartialSendFailure(_logger, nackedIds.Count, pending.Count);
            }

            LogDispatched(_logger, confirmedIds.Count);
        }
        finally
        {
            // Return rented buffers to ArrayPool — GetPendingAsync rents them via ArrayPool.Rent().
            // Skip ids the store retained: re-enqueued in-memory entries still reference the buffer,
            // so returning it here would be a use-after-return on the next dispatch.
            for (int i = 0; i < pending.Count; i++)
            {
                if (retainedByStore.Count == 0 || !retainedByStore.Contains(pending[i].Id))
                {
                    ArrayPool<byte>.Shared.Return(pending[i].PooledBody);
                }
            }
        }

        return (pending.Count, confirmedCount, nackedCount);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OutboxDispatcher started. PollingInterval={PollingInterval}, BatchSize={BatchSize}.")]
    private static partial void LogDispatcherStarted(
        ILogger logger,
        TimeSpan pollingInterval,
        int batchSize);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OutboxDispatcher stopping.")]
    private static partial void LogDispatcherStopping(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OutboxDispatcher stopped.")]
    private static partial void LogDispatcherStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dispatching {Count} pending outbox messages.")]
    private static partial void LogDispatching(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Successfully dispatched and marked {Count} outbox messages as delivered.")]
    private static partial void LogDispatched(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error during outbox dispatch batch. Will retry on next tick.")]
    private static partial void LogDispatchError(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{NackedCount} of {TotalCount} outbox messages were not confirmed by the broker; they were released for a deferred retry.")]
    private static partial void LogPartialSendFailure(ILogger logger, int nackedCount, int totalCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Per-key ordering barrier applied: {BlockedCount} confirmed sibling(s) behind a nacked head-of-line entry in their key group were released without deferral and will be sent again after the head of their key is delivered.")]
    private static partial void LogPerKeyBarrierApplied(ILogger logger, int blockedCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Outbox drain streak ended after {BatchCount} batch(es): {NackedCount} of {ClaimedCount} claimed messages were nacked; the streak cap was halved.")]
    private static partial void LogDrainStreakNackShare(
        ILogger logger,
        int batchCount,
        int nackedCount,
        int claimedCount);
}
