using System.Buffers;
using System.Collections.Concurrent;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Saga.Scheduling;

/// <summary>
/// <see cref="IScheduleProvider"/> backed by a transport adapter that supports native
/// broker-level scheduled delivery (probed via <c>transport as INativeMessageScheduler</c>).
/// Used for Azure Service Bus, where the broker natively schedules and cancels messages.
/// </summary>
/// <remarks>
/// <para>
/// The token map is keyed on <see cref="TokenKey"/> — the pair of saga correlationId and
/// timeout message type — so two different timeout types scheduled for the same saga instance
/// get independent entries: cancelling one type never cancels the other. The same provider
/// instance is shared by every event of one saga type (see <c>SagaScheduleProviderCache</c>),
/// which is what makes a later event able to find and cancel a token an earlier event stored.
/// </para>
/// <para>
/// The map is <b>in-process only</b>. After a process restart, <see cref="CancelAsync{T}"/>
/// will not find the token and logs a warning, returning without calling
/// <c>CancelScheduledAsync</c> (best-effort, same semantics as
/// <see cref="DelayRequeueScheduleProvider"/>).
/// </para>
/// <para>
/// The map is bounded: a time-based sweep evicts entries past <c>EnqueueAt + grace</c>, and a
/// hard size cap evicts the oldest entry on overflow. The time-based sweep only scans the whole
/// map once the eviction checkpoint has elapsed — never on every call. The size-cap path never
/// scans the map either: a min-heap keyed on <c>EvictAfter</c> finds the oldest entry in
/// O(log n), with stale heap candidates (from overwritten, cancelled, or time-evicted entries)
/// discarded lazily as they are popped; the heap itself is bounded and periodically compacted
/// back down when those stale candidates accumulate. Evicting a not-yet-due entry under the
/// cap is logged, rate-limited to at most one aggregated warning per interval.
/// </para>
/// </remarks>
internal sealed partial class TransportNativeScheduleProvider : IScheduleProvider
{
    // Key for the token map: a timeout is uniquely identified by which saga instance
    // (correlationId) scheduled it and which timeout type it is. Two different timeout types
    // scheduled for the same saga instance therefore get independent entries.
    internal readonly record struct TokenKey(Guid CorrelationId, Type MessageType);

    // Value stored per TokenKey: the broker token + the time after which the
    // entry is eligible for time-based eviction (EnqueueAt + grace period).
    internal readonly record struct TokenEntry(ScheduledMessageToken Token, DateTimeOffset EvictAfter);

    // Grace period after the scheduled enqueue time before an entry may be evicted.
    // Once past EnqueueAt + grace the message has been delivered (or expired) and any
    // cancel attempt would be a no-op on the broker anyway.
    private static readonly TimeSpan EvictionGrace = TimeSpan.FromMinutes(5);

    // Minimum spacing between two "live token evicted under the cap" warnings. Evictions that
    // happen more often than this are still counted, just folded into the next warning instead
    // of producing one log line each — keeps a sustained overflow from flooding the log.
    private static readonly TimeSpan LiveEvictionWarningInterval = TimeSpan.FromMinutes(1);

    // Hard cap on the number of in-flight token entries. When exceeded, the oldest
    // entry (smallest EvictAfter) is removed before inserting the new one.
    // Satisfies CLAUDE.md "No unbounded channels/buffers — always bounded with configurable limits".
    internal const int DefaultMaxTokens = 10_000;

    // Once the eviction heap (which never shrinks on its own — see _evictionHeap below)
    // accumulates more than this multiple of the cap in stale candidates, it is rebuilt from
    // the live map. Keeps the auxiliary structure's memory bounded and deterministic.
    private const int HeapCompactionMultiplier = 2;

    private readonly INativeMessageScheduler _scheduler;
    private readonly IMessageSerializer _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TransportNativeScheduleProvider> _logger;
    private readonly int _maxTokens;

    // Token map: TokenKey(correlationId, timeout type) → TokenEntry. The SAME TokenKey is
    // computed by both ScheduleAsync<T> (insert) and CancelAsync<T> (remove), fixing GAP-1 —
    // and keying on the timeout type as well as the correlationId means two different timeout
    // types scheduled for the same saga instance never collide.
    private readonly ConcurrentDictionary<TokenKey, TokenEntry> _tokens = new();

    // Live entry count, maintained by Interlocked increment/decrement — but ONLY on an insert
    // that actually adds a new key and a removal that actually takes an entry out. The
    // insert loop in ScheduleAsync distinguishes "added a new key" from "overwrote an existing
    // key" so a key re-inserted after a concurrent removal (TryAdd racing TryRemove) can never
    // drift the counter. Read instead of _tokens.Count (which takes every internal bucket lock)
    // by TokenCount and the size-cap check below.
    private int _count;

    // UTC ticks of the next time the time-based eviction scan is allowed to run. A scan sets
    // this to (now + EvictionGrace) after it runs; EvictStaleEntries skips the O(n) scan
    // entirely until this checkpoint elapses. The size cap (EnforceMaxSize) does NOT feed into
    // this checkpoint — it evicts via the O(log n) heap below instead of ever triggering a scan.
    private long _nextEvictionAtTicks;

    // Min-heap of (TokenKey, EvictAfter ticks) candidates, giving O(log n) access to the
    // oldest live entry for EnforceMaxSize instead of an O(n) scan over the whole map. Every
    // successful insert in ScheduleAsync pushes one candidate; overwriting, cancelling, or
    // time-evicting a key leaves its old candidate in the heap as a stale entry, which is
    // discarded lazily (by comparing against the map's CURRENT entry) the next time it is
    // popped. Guarded by _heapLock because PriorityQueue<,> is not thread-safe.
    private readonly PriorityQueue<TokenKey, long> _evictionHeap = new();
    private readonly Lock _heapLock = new();

    // Aggregation state for the "live token evicted under the cap" warning — all touched only
    // while holding _heapLock (from EnforceMaxSize), so plain fields suffice.
    private int _evictedSinceLastWarning;
    private long _lastLiveEvictionWarningAtTicks;
    private Guid _sampleEvictedCorrelationId;
    private string _sampleEvictedMessageType = string.Empty;

    internal TransportNativeScheduleProvider(
        INativeMessageScheduler scheduler,
        IMessageSerializer serializer,
        ILogger<TransportNativeScheduleProvider> logger,
        TimeProvider? timeProvider = null,
        int maxTokens = DefaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(logger);
        _scheduler = scheduler;
        _serializer = serializer;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxTokens = maxTokens > 0 ? maxTokens : DefaultMaxTokens;
    }

    /// <inheritdoc />
    public async Task ScheduleAsync<T>(
        T message,
        TimeSpan delay,
        string destinationQueue,
        Guid correlationId,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destinationQueue);

        var now = _timeProvider.GetUtcNow();
        var enqueueAt = now + delay;

        // Time-based sweep of entries past EnqueueAt + grace. Amortized — see EvictStaleEntries.
        EvictStaleEntries(now);

        // Size-cap enforcement — evict the oldest entry (via the heap) on overflow.
        EnforceMaxSize(now);

        // Serialize message. Not a hot path — ArrayBufferWriter allocation is acceptable here
        // (same pattern as DelayRequeueScheduleProvider, explicitly noted in plan §13).
        var writer = new ArrayBufferWriter<byte>();
        _serializer.Serialize(message, writer);
        ReadOnlyMemory<byte> body = writer.WrittenMemory.ToArray();

        var outbound = new OutboundMessage(
            routingKey: destinationQueue,
            headers: new Dictionary<string, string>
            {
                ["BW-MessageType"] = typeof(T).Name,
                ["message-id"] = Guid.NewGuid().ToString(),
                ["correlation-id"] = correlationId.ToString()
            },
            body: body,
            contentType: _serializer.ContentType);

        var token = await _scheduler.ScheduleAsync(outbound, enqueueAt, cancellationToken)
            .ConfigureAwait(false);

        // Key on (SAGA correlationId, timeout type) — the exact same key CancelAsync<T> will
        // look up. Scheduling the same (correlationId, T) pair again overwrites the prior entry.
        var key = new TokenKey(correlationId, typeof(T));
        var entry = new TokenEntry(token, enqueueAt + EvictionGrace);
        StoreToken(key, entry);

        LogScheduled(correlationId, typeof(T).Name, enqueueAt);
    }

    /// <inheritdoc />
    public async Task CancelAsync<T>(
        Guid correlationId,
        CancellationToken cancellationToken = default) where T : class
    {
        if (_tokens.TryRemove(new TokenKey(correlationId, typeof(T)), out TokenEntry entry))
        {
            Interlocked.Decrement(ref _count);
            await _scheduler.CancelScheduledAsync(entry.Token, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Best-effort: token not found (cross-process restart, already delivered, timeout
            // type mismatch, or never scheduled). Log warning and return — same semantics as
            // DelayRequeue.
            LogCancelTokenNotFound(correlationId, typeof(T).Name);
        }
    }

    // ── Internal helpers (accessible via InternalsVisibleTo in tests) ─────────

    /// <summary>
    /// Returns the current count of token entries. Exposed internally for test assertions.
    /// </summary>
    internal int TokenCount => Volatile.Read(ref _count);

    /// <summary>
    /// Returns the ConcurrentDictionary's own entry count (takes every internal bucket lock).
    /// Exposed internally so tests can assert <see cref="TokenCount"/> never drifts from the
    /// map's actual contents. Never used on a production code path — <see cref="TokenCount"/>
    /// is the cheap, Interlocked-backed equivalent used there.
    /// </summary>
    internal int DictionaryEntryCount => _tokens.Count;

    // ── Private helpers ───────────────────────────────────────────────────────

    // Stores (key, entry) in the map, incrementing _count if and only if the key did not
    // already exist. TryAdd and "overwrite an existing key" are two different outcomes that
    // must be told apart atomically: naively falling back to `_tokens[key] = entry` after a
    // failed TryAdd can re-insert a key that a concurrent CancelAsync/eviction removed between
    // the TryAdd and the fallback write, silently underreporting _count relative to the map's
    // real contents (PERF-3/SEC-2). The loop retries instead, so _count only ever changes on an
    // insert/removal that actually happened.
    private void StoreToken(TokenKey key, TokenEntry entry)
    {
        while (true)
        {
            if (_tokens.TryAdd(key, entry))
            {
                Interlocked.Increment(ref _count);
                break;
            }

            if (_tokens.TryGetValue(key, out TokenEntry existing) && _tokens.TryUpdate(key, entry, existing))
            {
                break;
            }

            // Either the key was removed concurrently after the TryAdd above failed (so
            // TryGetValue found nothing), or TryUpdate lost a race against another writer
            // (the value changed between TryGetValue and TryUpdate) — retry from the top.
        }

        EnqueueEvictionCandidate(key, entry);
    }

    // Time-based sweep: scans the whole map only once the eviction checkpoint has elapsed (now
    // is at or past _nextEvictionAtTicks) — never on every ScheduleAsync call, and never merely
    // because the map is near capacity (PERF-2; the size cap is handled by EnforceMaxSize's
    // heap instead, so a map saturated with live entries does not force a rescan here). A scan
    // that finds nothing to remove still advances the checkpoint, so an idle provider does not
    // re-scan on every subsequent call either. Concurrent scans are harmless: TryRemove is
    // idempotent, so a losing thread's removal attempt is simply a no-op.
    private void EvictStaleEntries(DateTimeOffset now)
    {
        if (now.UtcTicks < Interlocked.Read(ref _nextEvictionAtTicks))
        {
            return;
        }

        foreach (var (key, entry) in _tokens)
        {
            if (entry.EvictAfter <= now && _tokens.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _count);
            }
        }

        Interlocked.Exchange(ref _nextEvictionAtTicks, (now + EvictionGrace).UtcTicks);
    }

    // Size-cap enforcement. Cheap on the common path (a single Volatile.Read) and only touches
    // the heap while the map is actually at or over the cap. Finds the oldest live entry via
    // the min-heap in O(log n) instead of scanning the whole map (PERF-1): heap candidates are
    // popped in EvictAfter order and checked against the map's CURRENT entry for that key,
    // discarding stale candidates (left behind by overwrite/cancel/time-eviction) until a live
    // match is found and atomically removed.
    private void EnforceMaxSize(DateTimeOffset now)
    {
        if (Volatile.Read(ref _count) < _maxTokens)
        {
            return;
        }

        lock (_heapLock)
        {
            while (Volatile.Read(ref _count) >= _maxTokens)
            {
                if (!_evictionHeap.TryDequeue(out TokenKey key, out long evictAfterTicks))
                {
                    // The heap ran dry (e.g. every remaining candidate was already popped as
                    // stale) while the map still reports entries at/over the cap. Rebuild the
                    // heap from the live map and retry once before giving up.
                    CompactEvictionHeapNoLock();
                    if (!_evictionHeap.TryDequeue(out key, out evictAfterTicks))
                    {
                        break; // Map is empty (concurrent removal race) — nothing left to evict.
                    }
                }

                if (!_tokens.TryGetValue(key, out TokenEntry current) || current.EvictAfter.UtcTicks != evictAfterTicks)
                {
                    // Stale heap candidate: this key was overwritten, cancelled, or time-evicted
                    // since this candidate was pushed. Lazy deletion — discard and pop the next.
                    continue;
                }

                if (!_tokens.TryRemove(new KeyValuePair<TokenKey, TokenEntry>(key, current)))
                {
                    // Lost a race with a concurrent cancel/re-schedule between the read above
                    // and this compare-and-remove. Discard and pop the next candidate.
                    continue;
                }

                Interlocked.Decrement(ref _count);

                // The entry's own timeout has not fired yet (its enqueueAt is still in the
                // future) — evicting it under the size cap means it can no longer be cancelled.
                if (current.EvictAfter - EvictionGrace > now)
                {
                    RecordLiveTokenEvictionNoLock(key.CorrelationId, key.MessageType.Name, now);
                }
            }
        }
    }

    // Pushes one eviction candidate for the entry just stored. Must be called with the same
    // (key, entry) that StoreToken just wrote to _tokens — the heap's priority is EvictAfter's
    // UtcTicks, so EnforceMaxSize can tell a live heap candidate from a stale one just by
    // comparing against the map's current entry (see EnforceMaxSize).
    private void EnqueueEvictionCandidate(TokenKey key, TokenEntry entry)
    {
        lock (_heapLock)
        {
            _evictionHeap.Enqueue(key, entry.EvictAfter.UtcTicks);

            // Overwritten, cancelled, and time-evicted keys leave their old candidate behind as
            // heap garbage that is never proactively removed (only lazily discarded on pop).
            // Rebuilding from the live map bounds that garbage instead of letting the heap grow
            // without limit under sustained overwrite/cancel churn.
            if (_evictionHeap.Count > _maxTokens * HeapCompactionMultiplier)
            {
                CompactEvictionHeapNoLock();
            }
        }
    }

    // Rebuilds the heap from the live map's current entries. Must be called while holding
    // _heapLock. Runs only when the heap has grown past HeapCompactionMultiplier × the cap, so
    // it is rare — the map itself never holds more than _maxTokens live entries at a time.
    private void CompactEvictionHeapNoLock()
    {
        _evictionHeap.Clear();
        foreach (var (key, entry) in _tokens)
        {
            _evictionHeap.Enqueue(key, entry.EvictAfter.UtcTicks);
        }
    }

    // Records one live-token eviction and flushes an aggregated warning at most once per
    // LiveEvictionWarningInterval. Evictions that happen more often than that are still
    // counted (_evictedSinceLastWarning) and their sample identity kept up to date, but folded
    // into the NEXT warning instead of each producing their own log line. Must be called while
    // holding _heapLock.
    private void RecordLiveTokenEvictionNoLock(Guid correlationId, string messageType, DateTimeOffset now)
    {
        _evictedSinceLastWarning++;
        _sampleEvictedCorrelationId = correlationId;
        _sampleEvictedMessageType = messageType;

        if (now.UtcTicks - _lastLiveEvictionWarningAtTicks < LiveEvictionWarningInterval.Ticks)
        {
            return;
        }

        LogLiveTokenEvicted(_maxTokens, _evictedSinceLastWarning, _sampleEvictedMessageType, _sampleEvictedCorrelationId);
        _evictedSinceLastWarning = 0;
        _lastLiveEvictionWarningAtTicks = now.UtcTicks;
    }

    // ── Logging (source-gen partial methods) ─────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Native schedule: correlationId={CorrelationId} message={MessageType} enqueueAt={EnqueueAt:O}")]
    private partial void LogScheduled(Guid correlationId, string messageType, DateTimeOffset enqueueAt);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Cancel requested for correlationId={CorrelationId} ({MessageType}) but no scheduled token was found. " +
                  "The message may have already been delivered, never scheduled, or the token was lost on process restart. " +
                  "Cancel is a no-op (best-effort, same as DelayRequeue).")]
    private partial void LogCancelTokenNotFound(Guid correlationId, string messageType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Token map limit ({MaxTokens}) reached: evicted {EvictedCount} pending token(s) since the last " +
                  "warning (latest: {MessageType} for saga {CorrelationId}); those timeouts can no longer be " +
                  "cancelled and will still be delivered.")]
    private partial void LogLiveTokenEvicted(int maxTokens, int evictedCount, string messageType, Guid correlationId);
}
