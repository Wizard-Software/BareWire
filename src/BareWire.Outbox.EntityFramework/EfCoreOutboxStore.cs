using System.Buffers;
using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BareWire.Abstractions.Transport;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.EntityFrameworkCore;

namespace BareWire.Outbox.EntityFramework;

internal sealed class EfCoreOutboxStore : IOutboxStore
{
    private readonly OutboxDbContext _dbContext;
    private readonly string _instanceId;
    private readonly IOutboxSqlDialect _dialect;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IOutboxJitterSource _jitterSource;
    private OutboxNackDeferralSchedule? _nackSchedule;

    internal EfCoreOutboxStore(
        OutboxDbContext dbContext,
        OutboxInstanceId instanceId,
        IOutboxSqlDialect dialect,
        OutboxOptions options,
        TimeProvider? timeProvider = null,
        IOutboxJitterSource? jitterSource = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);

        _dbContext = dbContext;
        _instanceId = instanceId.Value;
        _dialect = dialect;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitterSource = jitterSource ?? SharedRandomOutboxJitterSource.Instance;
    }

    // The clock every time-dependent operation of this store reads — exactly once per operation, so
    // the claim timestamp and the stale-lock cutoff always derive from the same instant.
    internal TimeProvider TimeProvider => _timeProvider;

    // Randomness source for retry jitter.
    internal IOutboxJitterSource JitterSource => _jitterSource;

    public ValueTask SaveMessagesAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (OutboundMessage message in messages)
        {
            // Copy ReadOnlyMemory<byte> to byte[] for DB persistence — not a hot path per ADR-003.
            byte[] payload = message.Body.ToArray();

            // Serialize headers to JSON string.
            string? headersJson = message.Headers.Count > 0
                ? JsonSerializer.Serialize(message.Headers)
                : null;

            var entity = new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = message.RoutingKey,
                ContentType = message.ContentType,
                Payload = payload,
                Headers = headersJson,
                CreatedAt = now,
                OrderingKey = ResolveOrderingKey(message.Headers)
            };

            _dbContext.OutboxMessages.Add(entity);
        }

        // Do NOT call SaveChanges — the ambient transaction (TransactionalOutboxMiddleware) handles it.
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<OutboxEntry>> GetPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            return Array.Empty<OutboxEntry>();
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset staleCutoff = now - _options.OutboxLockTimeout;

        // Use the configured dialect's atomic claim only when it targets the active EF Core
        // provider — matched via the base DatabaseFacade.ProviderName API (keeps this package
        // provider-agnostic: no hard dependency on any provider package). Providers without a
        // matching dialect fall back to the non-atomic client-side claim.
        int staleOwnedCount;
        if (string.Equals(_dbContext.Database.ProviderName, _dialect.ProviderName, StringComparison.Ordinal))
        {
            staleOwnedCount = await ClaimWithDialectAsync(batchSize, now, staleCutoff, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (!await ClaimClientSideAsync(batchSize, now, staleCutoff, cancellationToken).ConfigureAwait(false))
            {
                return Array.Empty<OutboxEntry>();
            }

            // The client-side claim re-stamps every row this instance owns, so none of them is stale.
            staleOwnedCount = 0;
        }

        List<OutboxMessage> rows = await ReadOwnedBatchAsync(batchSize, staleCutoff, staleOwnedCount, cancellationToken)
            .ConfigureAwait(false);

        var entries = new List<OutboxEntry>(rows.Count);

        foreach (OutboxMessage row in rows)
        {
            // Rent a pooled buffer and copy payload — caller is responsible for returning to pool.
            byte[] pooledBody = ArrayPool<byte>.Shared.Rent(row.Payload.Length);
            row.Payload.CopyTo(pooledBody, 0);

            IReadOnlyDictionary<string, string> headers = row.Headers is not null
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers)
                    ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()
                : new Dictionary<string, string>();

            entries.Add(new OutboxEntry
            {
                Id = row.Id,
                RoutingKey = row.DestinationAddress,
                Headers = headers,
                PooledBody = pooledBody,
                BodyLength = row.Payload.Length,
                ContentType = row.ContentType,
                CreatedAt = row.CreatedAt,
                Status = OutboxEntryStatus.Pending,
                OrderingKey = row.OrderingKey
            });
        }

        return entries;
    }

    // Atomic fair claim through the provider dialect (PostgreSQL: FOR UPDATE SKIP LOCKED). Each claim
    // statement is an UPDATE whose affected-row count is the number of rows it claimed; the rows
    // themselves are read back by the shared SELECT (LockedBy == this instance). Deliberately separate
    // statements rather than UPDATE ... RETURNING: EF Core's ExecuteSqlAsync returns only an
    // affected-row count, and FromSql/SqlQuery wrap the statement in a subquery, where a
    // data-modifying statement cannot sit on PostgreSQL.
    //
    // A cycle runs up to three claim statements against its effective capacity (batch size minus the
    // rows this instance still validly owns):
    //   1. new rows, in id order, up to the new-row reservation;
    //   2. due retries (deferred nacks, abandoned claims) in the remaining capacity — in due-time order
    //      for a built-in dialect, through the public claim statement (id order) for a custom one;
    //   3. a top-up with new rows when step 1 filled its reservation and step 2 left capacity unused.
    // Returns the number of stale rows this instance owned before the claim; ReadOwnedBatchAsync
    // must not hand those back unless one of the steps re-claimed them.
    private async ValueTask<int> ClaimWithDialectAsync(
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset staleCutoff,
        CancellationToken cancellationToken)
    {
        // Rows this instance still owns (carry-forward, e.g. a batch whose send threw and was never
        // released). Only rows with a valid lock count against the batch: a stale own row is claimable
        // by any instance in step 2, so it must be re-claimed before it may be sent again. LockedAt is
        // compared client-side because not every EF Core provider translates DateTimeOffset
        // comparisons; the list is bounded by what this instance owns.
        List<DateTimeOffset?> ownedLocks = await _dbContext.Set<OutboxMessage>()
            .Where(m => m.LockedBy == _instanceId && m.DeliveredAt == null)
            .Select(m => m.LockedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int validOwnedCount = 0;
        foreach (DateTimeOffset? lockedAt in ownedLocks)
        {
            if (lockedAt is { } value && value >= staleCutoff)
            {
                validOwnedCount++;
            }
        }

        int staleOwnedCount = ownedLocks.Count - validOwnedCount;
        int effective = OutboxFairClaimPlan.GetEffectiveBatchSize(batchSize, validOwnedCount);
        if (effective == 0)
        {
            return staleOwnedCount;
        }

        // A single-slot cycle cannot tell before claiming whether both classes are waiting; an empty
        // step simply hands its capacity to the next one, so the drawn turn only decides who goes first.
        bool retryTurn = effective == 1 && OutboxFairClaimPlan.DrawSingleSlotRetryTurn(_jitterSource);
        int retryReserve = OutboxFairClaimPlan.GetRetryReserve(effective, retryTurn);
        BareWire.Abstractions.Outbox.OrderingMode mode = _options.OrderingMode;

        int newLimit = effective - retryReserve;
        int newClaimed = newLimit > 0
            ? await ExecuteClaimAsync(GetNewRowsClaimSql(now, newLimit, mode), newLimit, cancellationToken)
                .ConfigureAwait(false)
            : 0;

        int retryLimit = effective - newClaimed;
        int retryClaimed = 0;
        if (retryLimit > 0)
        {
            FormattableString retrySql = _dialect is IDueOrderedRetryClaimSql dueOrdered
                ? dueOrdered.GetDueOrderedRetryClaimSql(_instanceId, now, staleCutoff, retryLimit, mode)
                : _dialect.GetClaimSql(_instanceId, now, staleCutoff, retryLimit, mode);
            retryClaimed = await ExecuteClaimAsync(retrySql, retryLimit, cancellationToken).ConfigureAwait(false);
        }

        if (OutboxFairClaimPlan.ShouldTopUp(effective, retryReserve, newClaimed, retryClaimed))
        {
            int topUpLimit = effective - newClaimed - retryClaimed;
            FormattableString topUpSql = _dialect is INewRowsClaimSql newRows
                ? newRows.GetNewRowsClaimSql(_instanceId, now, topUpLimit, mode)
                : _dialect.GetClaimSql(_instanceId, now, staleCutoff, topUpLimit, mode);
            await ExecuteClaimAsync(topUpSql, topUpLimit, cancellationToken).ConfigureAwait(false);
        }

        return staleOwnedCount;
    }

    // New rows only: the built-in dialect's index-ordered shape, or the public claim statement with a
    // cutoff older than any lock, which admits only rows whose LockedAt is NULL.
    private FormattableString GetNewRowsClaimSql(
        DateTimeOffset now,
        int limit,
        BareWire.Abstractions.Outbox.OrderingMode mode)
        => _dialect is INewRowsClaimSql newRows
            ? newRows.GetNewRowsClaimSql(_instanceId, now, limit, mode)
            : _dialect.GetClaimSql(_instanceId, now, OutboxFairClaimPlan.NewRowsOnlyCutoff, limit, mode);

    private async ValueTask<int> ExecuteClaimAsync(
        FormattableString sql,
        int limit,
        CancellationToken cancellationToken)
    {
        int reported = await _dbContext.Database.ExecuteSqlAsync(sql, cancellationToken).ConfigureAwait(false);
        return OutboxFairClaimPlan.ClampClaimed(reported, limit);
    }

    // SQLite / other providers without a matching dialect: two-step, non-concurrent claim — safe for a
    // single dispatcher instance (tests, development), not for multi-instance production. Candidate ids
    // are selected in memory (Take() in ExecuteUpdateAsync is not supported, and SQLite EF does not
    // translate nullable DateTimeOffset comparisons), split into the same classes as the dialect path,
    // then marked as claimed with one UPDATE. Returns false when there was nothing to claim.
    private async ValueTask<bool> ClaimClientSideAsync(
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset staleCutoff,
        CancellationToken cancellationToken)
    {
        var pendingRows = await _dbContext.Set<OutboxMessage>()
            .Where(m => m.DeliveredAt == null)
            .Select(m => new { m.Id, m.LockedAt, m.LockedBy, m.OrderingKey })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Rows already owned by this instance are always claimable (their lock is refreshed); others
        // only when never claimed or when their lock is stale.
        var candidates = pendingRows
            .Where(x => x.LockedBy == _instanceId
                || x.LockedAt is null
                || x.LockedAt.Value < staleCutoff);

        if (_options.OrderingMode == BareWire.Abstractions.Outbox.OrderingMode.PerKey)
        {
            // Head-of-line filter, applied before the batch split so blocked rows consume no slots. The
            // heads are the minimum Id per key among ALL undelivered rows (not only claimable ones); a
            // keyed row is claimable only when it is the head of its key. Keyless rows always pass.
            Dictionary<string, long> headIdPerKey = pendingRows
                .Where(x => x.OrderingKey is not null)
                .GroupBy(x => x.OrderingKey!)
                .ToDictionary(g => g.Key, g => g.Min(x => x.Id));

            candidates = candidates.Where(x =>
                x.OrderingKey is null
                || (headIdPerKey.TryGetValue(x.OrderingKey, out long headId) && x.Id == headId));
        }

        var carried = new List<long>();
        var fresh = new List<long>();
        var retries = new List<(DateTimeOffset LockedAt, long Id)>();

        foreach (var row in candidates)
        {
            if (row.LockedBy == _instanceId)
            {
                carried.Add(row.Id);
            }
            else if (row.LockedAt is { } lockedAt)
            {
                retries.Add((lockedAt, row.Id));
            }
            else
            {
                fresh.Add(row.Id);
            }
        }

        carried.Sort();
        if (carried.Count > batchSize)
        {
            carried.RemoveRange(batchSize, carried.Count - batchSize);
        }

        int effective = OutboxFairClaimPlan.GetEffectiveBatchSize(batchSize, carried.Count);
        bool retryTurn = effective == 1
            && fresh.Count > 0
            && retries.Count > 0
            && OutboxFairClaimPlan.DrawSingleSlotRetryTurn(_jitterSource);
        int retryReserve = OutboxFairClaimPlan.GetRetryReserve(effective, retryTurn);
        (int newTake, int retryTake) = OutboxFairClaimPlan.SplitCandidates(
            effective,
            retryReserve,
            fresh.Count,
            retries.Count);

        // New rows in id order; due retries in due-time order (earliest LockedAt first, then id).
        fresh.Sort();
        retries.Sort((a, b) =>
        {
            int byDueTime = a.LockedAt.CompareTo(b.LockedAt);
            return byDueTime != 0 ? byDueTime : a.Id.CompareTo(b.Id);
        });

        var claimableIds = new List<long>(carried.Count + newTake + retryTake);
        claimableIds.AddRange(carried);
        claimableIds.AddRange(fresh.Take(newTake));
        claimableIds.AddRange(retries.Take(retryTake).Select(r => r.Id));

        if (claimableIds.Count == 0)
        {
            return false;
        }

        await _dbContext.Set<OutboxMessage>()
            .Where(m => claimableIds.Contains(m.Id))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.LockedAt, now)
                    .SetProperty(m => m.LockedBy, _instanceId),
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    // Reads the claimed batch: LockedBy == this instance AND not yet delivered, oldest first.
    // Do NOT compare LockedAt == now — timestamptz precision truncation causes equality to fail.
    // Take(batchSize) bounds the returned batch (and the pooled buffers rented for it). When this
    // instance owned stale rows before the claim, those rows are claimable by any instance, so they are
    // returned only if a claim step of this cycle re-stamped them. Their number is not bounded by the
    // batch size (a batch whose send keeps throwing is never released), so the lock check runs on a
    // lightweight (Id, LockedAt) projection and only the kept ids — at most batchSize — load their
    // payloads. LockedAt is compared client-side because not every EF Core provider translates
    // DateTimeOffset comparisons.
    private async ValueTask<List<OutboxMessage>> ReadOwnedBatchAsync(
        int batchSize,
        DateTimeOffset staleCutoff,
        int staleOwnedCount,
        CancellationToken cancellationToken)
    {
        IQueryable<OutboxMessage> owned = _dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(m => m.LockedBy == _instanceId && m.DeliveredAt == null)
            .OrderBy(m => m.Id);

        if (staleOwnedCount == 0)
        {
            return await owned.Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var ownedLocks = await owned
            .Select(m => new { m.Id, m.LockedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var validIds = new List<long>(Math.Min(batchSize, ownedLocks.Count));
        foreach (var row in ownedLocks)
        {
            if (row.LockedAt is { } lockedAt && lockedAt >= staleCutoff)
            {
                validIds.Add(row.Id);
                if (validIds.Count == batchSize)
                {
                    break;
                }
            }
        }

        if (validIds.Count == 0)
        {
            return [];
        }

        return await _dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(m => validIds.Contains(m.Id) && m.LockedBy == _instanceId && m.DeliveredAt == null)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask MarkDeliveredAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        DateTimeOffset deliveredAt = _timeProvider.GetUtcNow();

        await _dbContext.Set<OutboxMessage>()
            .Where(m => ids.Contains(m.Id))
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.DeliveredAt, deliveredAt),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => ReleaseLockAsync(ids, [], cancellationToken);

    public async ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> nackedIds,
        IReadOnlyList<long> barrierReleasedIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nackedIds);
        ArgumentNullException.ThrowIfNull(barrierReleasedIds);

        if (nackedIds.Count == 0 && barrierReleasedIds.Count == 0)
        {
            return FrozenSet<long>.Empty;
        }

        IReadOnlyList<long> releasedIds = ResolveReleasedIds(nackedIds, barrierReleasedIds);

        Expression<Func<OutboxMessage, DateTimeOffset?>>? deferredLockedAt = null;
        Expression<Func<OutboxMessage, int>>? incrementedRetryCount = null;

        if (nackedIds.Count > 0)
        {
            // Created lazily: this store is scoped per consume scope, and most scopes never nack.
            _nackSchedule ??= OutboxNackDeferralSchedule.FromOptions(_options, _jitterSource);

            // With both kinds in one call, only the nacked ids are deferred and counted; an id in both
            // lists evaluates as nacked, so a rejected row is never released without its deferral.
            Expression<Func<OutboxMessage, bool>>? isNacked = barrierReleasedIds.Count > 0
                ? m => nackedIds.Contains(m.Id)
                : null;

            (deferredLockedAt, incrementedRetryCount) = BuildNackSetters(
                _nackSchedule.CreatePlan(),
                _timeProvider.GetUtcNow(),
                isNacked);
        }

        // Both kinds are released by ONE statement, so there is no window in which the nacked head of an
        // ordering key is already released while its barrier-held siblings are still owned by this
        // instance (and would be re-sent ahead of it on the next cycle). The LockedBy == _instanceId
        // filter ensures an instance never releases another instance's claim, which preserves the
        // no-double-delivery guarantee across instances; DeliveredAt == null guards against a race with
        // MarkDeliveredAsync. A nacked row gets LockedAt = now - OutboxLockTimeout + deferral, so the
        // claim predicate LockedAt < now' - OutboxLockTimeout keeps it unclaimable until the deferral
        // has elapsed; a barrier-released row gets LockedAt = null and is claimable on the next cycle.
        await _dbContext.Set<OutboxMessage>()
            .Where(m => releasedIds.Contains(m.Id) && m.LockedBy == _instanceId && m.DeliveredAt == null)
            .ExecuteUpdateAsync(
                setters =>
                {
                    setters.SetProperty(m => m.LockedBy, (string?)null);

                    if (deferredLockedAt is null || incrementedRetryCount is null)
                    {
                        setters.SetProperty(m => m.LockedAt, (DateTimeOffset?)null);
                        return;
                    }

                    setters.SetProperty(m => m.LockedAt, deferredLockedAt);
                    setters.SetProperty(m => m.RetryCount, incrementedRetryCount);
                },
                cancellationToken)
            .ConfigureAwait(false);

        // GetPendingAsync copies each row into a fresh per-cycle pooled buffer, so this store
        // retains no caller buffers — the dispatcher must return all of them.
        return FrozenSet<long>.Empty;
    }

    private static IReadOnlyList<long> ResolveReleasedIds(
        IReadOnlyList<long> nackedIds,
        IReadOnlyList<long> barrierReleasedIds)
    {
        if (barrierReleasedIds.Count == 0)
        {
            return nackedIds;
        }

        if (nackedIds.Count == 0)
        {
            return barrierReleasedIds;
        }

        var union = new HashSet<long>(nackedIds);
        union.UnionWith(barrierReleasedIds);
        return [.. union];
    }

    // Builds the LockedAt and RetryCount setter values for nacked rows. When isNacked is given (a call
    // that also carries barrier-released ids), rows it does not match keep LockedAt = null and their
    // RetryCount unchanged.
    //
    // LockedAt is one flat conditional over the row's retry bucket (RetryCount == 0, == 1, ..., otherwise
    // the escalation cap) and jitter bucket (Id % JitterBucketCount), which EF Core translates into a
    // single CASE expression. Identity ids are positive, so SQL's Id % 4 equals the schedule's
    // normalized jitter bucket. Every cell value is read through a StrongBox field — the shape the C#
    // compiler emits for a captured variable — so EF Core sends it as a query parameter rather than
    // inlining a literal: the SQL text and the EF Core query-cache key stay stable across releases (no
    // re-translation per release, and prepared statements can be reused where the provider enables them).
    //
    // RetryCount saturates at int.MaxValue instead of overflowing: an overflowed value would make the
    // row unreadable (or fail the whole statement) and stall this instance's dispatch.
    private static (Expression<Func<OutboxMessage, DateTimeOffset?>> LockedAt,
        Expression<Func<OutboxMessage, int>> RetryCount) BuildNackSetters(
        OutboxNackDeferralPlan plan,
        DateTimeOffset now,
        Expression<Func<OutboxMessage, bool>>? isNacked)
    {
        ParameterExpression row = isNacked?.Parameters[0] ?? Expression.Parameter(typeof(OutboxMessage), "m");
        MemberExpression retryCount = Expression.Property(row, nameof(OutboxMessage.RetryCount));
        Expression jitterBucket = Expression.Modulo(
            Expression.Property(row, nameof(OutboxMessage.Id)),
            Expression.Constant((long)plan.JitterBucketCount));

        int capRetryBucket = plan.RetryBucketCount - 1;
        int lastJitterBucket = plan.JitterBucketCount - 1;

        // Built from the innermost ELSE outwards, so the outermost test is (retry 0, jitter 0).
        Expression lockedAt = Cell(plan, now, capRetryBucket, lastJitterBucket);
        for (int retryBucket = capRetryBucket; retryBucket >= 0; retryBucket--)
        {
            int topJitterBucket = retryBucket == capRetryBucket ? lastJitterBucket - 1 : lastJitterBucket;
            for (int bucket = topJitterBucket; bucket >= 0; bucket--)
            {
                Expression test = Expression.Equal(jitterBucket, Expression.Constant((long)bucket));
                if (retryBucket < capRetryBucket)
                {
                    test = Expression.AndAlso(
                        Expression.Equal(retryCount, Expression.Constant(retryBucket)),
                        test);
                }

                lockedAt = Expression.Condition(test, Cell(plan, now, retryBucket, bucket), lockedAt);
            }
        }

        Expression incremented = Expression.Condition(
            Expression.LessThan(retryCount, Expression.Constant(int.MaxValue)),
            Expression.Add(retryCount, Expression.Constant(1)),
            retryCount);

        if (isNacked is not null)
        {
            lockedAt = Expression.Condition(isNacked.Body, lockedAt, Expression.Constant(null, typeof(DateTimeOffset?)));
            incremented = Expression.Condition(isNacked.Body, incremented, retryCount);
        }

        return (
            Expression.Lambda<Func<OutboxMessage, DateTimeOffset?>>(lockedAt, row),
            Expression.Lambda<Func<OutboxMessage, int>>(incremented, row));
    }

    private static MemberExpression Cell(OutboxNackDeferralPlan plan, DateTimeOffset now, int retryBucket, int jitterBucket)
        => Expression.Field(
            Expression.Constant(
                new StrongBox<DateTimeOffset?>(plan.GetDeferredLockedAt(now, retryBucket, jitterBucket))),
            nameof(StrongBox<DateTimeOffset?>.Value));

    public async ValueTask CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - retention;

        await _dbContext.Set<OutboxMessage>()
            .Where(m => m.DeliveredAt != null && m.DeliveredAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // Promotes the ordering key from the message headers when PerKey mode is active.
    // Rules (SEC-2 / §2.4 of the R7.7 plan):
    //   - Only active when OrderingMode == PerKey.
    //   - Key must be present in headers, non-whitespace, and <= 256 characters.
    //   - Keys longer than 256 characters produce null (keyless/passthrough) — NEVER truncated,
    //     because truncation would collapse distinct long keys to the same stored value, creating
    //     a head-of-line collision vector and an anti-pattern for tenant-isolation use cases.
    private string? ResolveOrderingKey(IReadOnlyDictionary<string, string> headers)
    {
        if (_options.OrderingMode != BareWire.Abstractions.Outbox.OrderingMode.PerKey)
        {
            return null;
        }

        string? headerName = _options.OrderingKeyHeaderName;
        if (string.IsNullOrEmpty(headerName))
        {
            return null;
        }

        if (!headers.TryGetValue(headerName, out string? key))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        // Over-limit keys become keyless (passthrough). Never truncate — see comment above.
        if (key.Length > 256)
        {
            return null;
        }

        return key;
    }
}
