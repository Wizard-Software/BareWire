using System.Diagnostics;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;

namespace BareWire.IntegrationTests.Outbox.InMemoryTransport;

/// <summary>
/// Records, across the lifetime of one <c>OutboxInMemoryHost</c>, which dispatch cycles claimed each
/// outbox row, when each cycle started, which cycle delivered a row, and how many times a row was
/// released by the per-key ordering barrier.
/// </summary>
/// <remarks>
/// <para>
/// Rows are identified by their stable <c>message-id</c> header — the same value across every resend of
/// a row — never by the store-assigned <see cref="OutboxEntry.Id"/>, which callers never see reused
/// across a resend the way the header is.
/// </para>
/// <para>
/// <b>Singleton state, scoped decorator.</b> This type is registered as a DI singleton and holds all
/// recorded state; <see cref="Wrap"/> returns a lightweight decorator implementing <c>IOutboxStore</c>
/// that delegates every call to the wrapped store and records into this shared state. The dispatcher
/// resolves a fresh <c>IOutboxStore</c> from a new DI scope on every cycle, so the recording state
/// itself must live outside any single scope.
/// </para>
/// <para>
/// <b>Concurrency.</b> Every accessor is safe to call while the dispatcher loop is running concurrently
/// on a different thread — all state is guarded by a single <see cref="Lock"/>, because tests read this
/// recorder mid-run, not only after the loop has stopped.
/// </para>
/// <para>
/// <b>Cycle counting.</b> Every <c>GetPendingAsync</c> call counts as one cycle, including an empty one
/// — the decorator never implements <c>IOutboxRetryBacklogProbe</c>, so the dispatcher's lazy gauge
/// sampling (which does not call <c>GetPendingAsync</c>) never inflates the count.
/// </para>
/// </remarks>
internal sealed class DispatchCycleRecorder
{
    private const string MessageIdHeader = "message-id";

    private readonly Lock _lock = new();
    private readonly List<long> _cycleStartTimestamps = [];
    private readonly Dictionary<long, Guid> _idToMessageId = new();
    private readonly Dictionary<Guid, List<int>> _attemptCycles = new();
    private readonly Dictionary<Guid, List<long>> _attemptTimestamps = new();
    private readonly Dictionary<Guid, int> _deliveredInCycle = new();
    private readonly Dictionary<Guid, int> _barrierReleasedCount = new();

    /// <summary>Number of <c>GetPendingAsync</c> calls observed so far, including empty ones.</summary>
    internal int CycleCount
    {
        get
        {
            lock (_lock)
            {
                return _cycleStartTimestamps.Count;
            }
        }
    }

    /// <summary>The <see cref="Stopwatch.GetTimestamp"/> value recorded at the start of the given 1-based cycle.</summary>
    internal long CycleStartTimestamp(int cycle)
    {
        lock (_lock)
        {
            return _cycleStartTimestamps[cycle - 1];
        }
    }

    /// <summary>The 1-based cycles in which the row identified by <paramref name="messageId"/> was claimed, in cycle order.</summary>
    internal IReadOnlyList<int> AttemptCyclesOf(Guid messageId)
    {
        lock (_lock)
        {
            return _attemptCycles.TryGetValue(messageId, out List<int>? cycles) ? [.. cycles] : [];
        }
    }

    /// <summary>The <see cref="Stopwatch.GetTimestamp"/> values recorded for every claim of <paramref name="messageId"/>, in cycle order.</summary>
    internal IReadOnlyList<long> AttemptTimestampsOf(Guid messageId)
    {
        lock (_lock)
        {
            return _attemptTimestamps.TryGetValue(messageId, out List<long>? timestamps) ? [.. timestamps] : [];
        }
    }

    /// <summary>The cycle whose <c>MarkDeliveredAsync</c> call carried the row, or <see langword="null"/> when it has not been delivered yet.</summary>
    internal int? DeliveredInCycle(Guid messageId)
    {
        lock (_lock)
        {
            return _deliveredInCycle.TryGetValue(messageId, out int cycle) ? cycle : null;
        }
    }

    /// <summary>The number of times the row was released by the per-key ordering barrier (passed as a <c>barrierReleasedIds</c> entry to <c>ReleaseLockAsync</c>).</summary>
    internal int BarrierReleasedCount(Guid messageId)
    {
        lock (_lock)
        {
            return _barrierReleasedCount.TryGetValue(messageId, out int count) ? count : 0;
        }
    }

    /// <summary>
    /// A consistent snapshot, taken under this recorder's own lock, of every row's attempt cycles
    /// observed so far. Use this instead of repeated <see cref="AttemptCyclesOf"/> calls when several
    /// rows' attempt counts must reflect the exact same instant of the dispatcher loop.
    /// </summary>
    internal IReadOnlyDictionary<Guid, IReadOnlyList<int>> SnapshotAttempts()
    {
        lock (_lock)
        {
            Dictionary<Guid, IReadOnlyList<int>> snapshot = new(_attemptCycles.Count);
            foreach ((Guid messageId, List<int> cycles) in _attemptCycles)
            {
                snapshot[messageId] = [.. cycles];
            }

            return snapshot;
        }
    }

    /// <summary>Wraps <paramref name="inner"/> in a scoped decorator that records into this instance's shared state.</summary>
    internal IOutboxStore Wrap(IOutboxStore inner) => new Decorator(inner, this);

    private void BeginCycle(out int cycle, out long timestamp)
    {
        lock (_lock)
        {
            timestamp = Stopwatch.GetTimestamp();
            _cycleStartTimestamps.Add(timestamp);
            cycle = _cycleStartTimestamps.Count;
        }
    }

    private void RegisterClaimed(int cycle, long timestamp, IReadOnlyList<OutboxEntry> batch)
    {
        lock (_lock)
        {
            foreach (OutboxEntry entry in batch)
            {
                Guid messageId = ParseMessageId(entry);
                _idToMessageId[entry.Id] = messageId;

                if (!_attemptCycles.TryGetValue(messageId, out List<int>? cycles))
                {
                    cycles = [];
                    _attemptCycles[messageId] = cycles;
                }

                cycles.Add(cycle);

                if (!_attemptTimestamps.TryGetValue(messageId, out List<long>? timestamps))
                {
                    timestamps = [];
                    _attemptTimestamps[messageId] = timestamps;
                }

                timestamps.Add(timestamp);
            }
        }
    }

    private void RecordDelivered(IReadOnlyList<long> ids)
    {
        lock (_lock)
        {
            int cycle = _cycleStartTimestamps.Count;
            foreach (long id in ids)
            {
                if (_idToMessageId.TryGetValue(id, out Guid messageId))
                {
                    _deliveredInCycle[messageId] = cycle;
                }
            }
        }
    }

    private void RecordBarrierReleased(IReadOnlyList<long> ids)
    {
        lock (_lock)
        {
            foreach (long id in ids)
            {
                if (_idToMessageId.TryGetValue(id, out Guid messageId))
                {
                    _barrierReleasedCount[messageId] = _barrierReleasedCount.GetValueOrDefault(messageId) + 1;
                }
            }
        }
    }

    private static Guid ParseMessageId(OutboxEntry entry)
        => entry.Headers.TryGetValue(MessageIdHeader, out string? value) && Guid.TryParse(value, out Guid messageId)
            ? messageId
            : throw new InvalidOperationException(
                $"Outbox entry {entry.Id} has no parseable '{MessageIdHeader}' header — every row seeded " +
                "through OutboxInMemoryHost.SaveRowAsync/SaveRowsAsync carries one.");

    // Scoped decorator resolved fresh by the dispatcher on every cycle (IServiceScopeFactory.CreateAsyncScope).
    // Delegates every call to `inner` and records into the shared DispatchCycleRecorder state. Deliberately
    // does NOT implement IOutboxRetryBacklogProbe — this recorder's job is cycle/attempt bookkeeping only,
    // never to change which capabilities the dispatcher sees on the store it wraps.
    private sealed class Decorator(IOutboxStore inner, DispatchCycleRecorder recorder) : IOutboxStore
    {
        public ValueTask SaveMessagesAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => inner.SaveMessagesAsync(messages, cancellationToken);

        public async ValueTask<IReadOnlyList<OutboxEntry>> GetPendingAsync(
            int batchSize, CancellationToken cancellationToken = default)
        {
            recorder.BeginCycle(out int cycle, out long timestamp);

            IReadOnlyList<OutboxEntry> batch = await inner.GetPendingAsync(batchSize, cancellationToken)
                .ConfigureAwait(false);
            recorder.RegisterClaimed(cycle, timestamp, batch);
            return batch;
        }

        public async ValueTask MarkDeliveredAsync(
            IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        {
            await inner.MarkDeliveredAsync(ids, cancellationToken).ConfigureAwait(false);
            recorder.RecordDelivered(ids);
        }

        public ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
            IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
            => ReleaseLockAsync(ids, [], cancellationToken);

        public async ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
            IReadOnlyList<long> nackedIds,
            IReadOnlyList<long> barrierReleasedIds,
            CancellationToken cancellationToken = default)
        {
            // The retained-buffer set is passed through unchanged — losing it would make the dispatcher
            // return an ArrayPool buffer the wrapped store still references (see OutboxDispatcher's
            // retainedByStore handling), corrupting the next dispatch's message content.
            IReadOnlySet<long> retained = await inner
                .ReleaseLockAsync(nackedIds, barrierReleasedIds, cancellationToken)
                .ConfigureAwait(false);
            recorder.RecordBarrierReleased(barrierReleasedIds);
            return retained;
        }

        public ValueTask CleanupAsync(TimeSpan retention, CancellationToken cancellationToken = default)
            => inner.CleanupAsync(retention, cancellationToken);
    }
}
