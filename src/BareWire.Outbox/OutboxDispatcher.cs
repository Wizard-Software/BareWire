using System.Buffers;
using System.Collections.Frozen;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox;

internal sealed partial class OutboxDispatcher : IHostedService, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITransportAdapter _adapter;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        ITransportAdapter adapter,
        OutboxOptions options,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = RunPollingLoopAsync(_cts.Token);
        LogDispatcherStarted(_logger, _options.PollingInterval, _options.DispatchBatchSize);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        LogDispatcherStopping(_logger);

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
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task RunPollingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollingInterval);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await DispatchBatchAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDispatchError(_logger, ex);
                // Continue — do not crash the hosted service on transient errors.
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IOutboxStore store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        IReadOnlyList<OutboxEntry> pending = await store
            .GetPendingAsync(_options.DispatchBatchSize, ct)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return;
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

            // R7.7.6 — Per-key barrier: when PerKey ordering is active and at least one entry was
            // nacked, apply head-of-line blocking per key. Any confirmed sibling with a higher Id
            // than the first nacked entry in its key group is moved to "release" — it must not be
            // marked delivered until the nacked head is retried and confirmed. Keyless entries
            // (OrderingKey == null) are always independent and are never blocked.
            //
            // The None path is bit-identical to pre-R7.7.6: no Dictionary allocation, no extra
            // branches taken — zero overhead on the default ordering mode.
            if (_options.OrderingMode == OrderingMode.PerKey && nackedIds.Count > 0)
            {
                // Rebuild confirmedIds and releaseIds applying per-key head-of-line blocking.
                // Keyless entries (OrderingKey == null) are handled in a separate pre-pass so
                // that the keyed dictionary uses non-nullable string keys (satisfying the
                // notnull TKey constraint and avoiding any nullable-analysis noise).
                confirmedIds = new List<long>(pending.Count);
                List<long> releaseIds = [];
                int blockedCount = 0;

                // Pre-pass: keyless entries are always independent — route them directly.
                for (int i = 0; i < pending.Count; i++)
                {
                    if (pending[i].OrderingKey is null)
                    {
                        if (results[i].IsConfirmed) confirmedIds.Add(ids[i]);
                        else releaseIds.Add(ids[i]);
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

                    if (firstNackedId == long.MaxValue)
                    {
                        // No nack in this group — all entries confirmed, no barrier.
                        foreach ((long id, bool _) in group) confirmedIds.Add(id);
                    }
                    else
                    {
                        // Barrier at firstNackedId: entries strictly before it are confirmed;
                        // entries at or after it are released (nacked head + blocked siblings).
                        foreach ((long id, bool _) in group)
                        {
                            if (id < firstNackedId)
                            {
                                confirmedIds.Add(id);
                            }
                            else
                            {
                                releaseIds.Add(id);
                                blockedCount++;
                            }
                        }

                        // The nacked head itself is not a "blocked sibling" — exclude it from
                        // the count so the log reflects only the held-back confirmed siblings.
                        blockedCount--;
                    }
                }

                nackedIds = releaseIds;

                if (blockedCount > 0)
                {
                    LogPerKeyBarrierApplied(_logger, blockedCount);
                }
            }

            if (confirmedIds.Count > 0)
            {
                await store.MarkDeliveredAsync(confirmedIds, ct).ConfigureAwait(false);
            }

            if (nackedIds.Count > 0)
            {
                // Explicitly release the per-instance lock on nacked rows so they are re-claimed on
                // the next poll cycle (~PollingInterval) instead of waiting for OutboxLockTimeout.
                retainedByStore = await store.ReleaseLockAsync(nackedIds, ct).ConfigureAwait(false);
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
        Message = "{NackedCount} of {TotalCount} outbox messages were not confirmed by the broker; their locks were released for retry on the next poll cycle.")]
    private static partial void LogPartialSendFailure(ILogger logger, int nackedCount, int totalCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Per-key ordering barrier applied: {BlockedCount} confirmed sibling(s) were held back due to a nacked head-of-line entry in their key group and will retry on the next poll cycle.")]
    private static partial void LogPerKeyBarrierApplied(ILogger logger, int blockedCount);
}
