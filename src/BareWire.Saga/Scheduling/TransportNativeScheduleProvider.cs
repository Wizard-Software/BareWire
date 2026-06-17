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
/// The <c>correlationId → token</c> map is <b>in-process only</b>. After a process restart,
/// <see cref="CancelAsync{T}"/> will not find the token and logs a warning, returning without
/// calling <c>CancelScheduledAsync</c> (best-effort, same semantics as
/// <see cref="DelayRequeueScheduleProvider"/>). Persistent token storage keyed on the saga
/// correlationId is a planned follow-up (see ADR-012 / OQ-1).
/// </para>
/// <para>
/// The map is bounded (time-based eviction past <c>EnqueueAt + grace</c> plus a configurable
/// size cap), satisfying the "no unbounded buffers" rule — see ADR-012 §Decision.6.
/// </para>
/// </remarks>
internal sealed partial class TransportNativeScheduleProvider : IScheduleProvider
{
    // Value stored per correlationId: the broker token + the time after which the
    // entry is eligible for time-based eviction (EnqueueAt + grace period).
    internal readonly record struct TokenEntry(ScheduledMessageToken Token, DateTimeOffset EvictAfter);

    // Grace period after the scheduled enqueue time before an entry may be evicted.
    // Once past EnqueueAt + grace the message has been delivered (or expired) and any
    // cancel attempt would be a no-op on the broker anyway.
    private static readonly TimeSpan EvictionGrace = TimeSpan.FromMinutes(5);

    // Hard cap on the number of in-flight token entries. When exceeded, the oldest
    // entry (smallest EvictAfter) is removed before inserting the new one.
    // Satisfies CLAUDE.md "No unbounded channels/buffers — always bounded with configurable limits".
    internal const int DefaultMaxTokens = 10_000;

    private readonly INativeMessageScheduler _scheduler;
    private readonly IMessageSerializer _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TransportNativeScheduleProvider> _logger;
    private readonly int _maxTokens;

    // Token map: correlationId → TokenEntry. The SAME correlationId is used by both
    // ScheduleAsync (insert) and CancelAsync<T> (remove), fixing GAP-1.
    private readonly ConcurrentDictionary<Guid, TokenEntry> _tokens = new();

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

        // PERF-1: evict stale entries (past EnqueueAt + grace) on each ScheduleAsync call.
        EvictStaleEntries(now);

        // PERF-1: enforce max-size cap — remove the oldest entry on overflow.
        EnforceMaxSize();

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

        // Key on the SAGA correlationId — the exact same value CancelAsync<T> will look up.
        _tokens[correlationId] = new TokenEntry(token, enqueueAt + EvictionGrace);

        LogScheduled(correlationId, typeof(T).Name, enqueueAt);
    }

    /// <inheritdoc />
    public async Task CancelAsync<T>(
        Guid correlationId,
        CancellationToken cancellationToken = default) where T : class
    {
        if (_tokens.TryRemove(correlationId, out TokenEntry entry))
        {
            await _scheduler.CancelScheduledAsync(entry.Token, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Best-effort: token not found (cross-process restart, already delivered, or
            // never scheduled). Log warning and return — same semantics as DelayRequeue.
            LogCancelTokenNotFound(correlationId, typeof(T).Name);
        }
    }

    // ── Internal helpers (accessible via InternalsVisibleTo in tests) ─────────

    /// <summary>
    /// Returns the current count of token entries. Exposed internally for test assertions.
    /// </summary>
    internal int TokenCount => _tokens.Count;

    // ── Private helpers ───────────────────────────────────────────────────────

    private void EvictStaleEntries(DateTimeOffset now)
    {
        foreach (var (key, entry) in _tokens)
        {
            if (entry.EvictAfter <= now)
            {
                _tokens.TryRemove(key, out _);
            }
        }
    }

    private void EnforceMaxSize()
    {
        // Loop until the map is within bounds. Under ConcurrentDictionary concurrency a
        // transient overshoot is possible; the loop restores the cap on each call.
        while (_tokens.Count >= _maxTokens)
        {
            // Find the entry with the smallest EvictAfter (oldest scheduled message).
            // Seed from the first enumerated entry so that an entry with EvictAfter ==
            // DateTimeOffset.MaxValue (from an extreme/overflowing delay) is still eligible.
            Guid? oldest = null;
            DateTimeOffset oldestTime = DateTimeOffset.MaxValue;
            bool first = true;

            foreach (var (key, entry) in _tokens)
            {
                if (first || entry.EvictAfter <= oldestTime)
                {
                    oldestTime = entry.EvictAfter;
                    oldest = key;
                    first = false;
                }
            }

            if (!oldest.HasValue)
            {
                // Dictionary is empty — nothing to evict (concurrent removal race).
                break;
            }

            _tokens.TryRemove(oldest.Value, out _);
        }
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
}
