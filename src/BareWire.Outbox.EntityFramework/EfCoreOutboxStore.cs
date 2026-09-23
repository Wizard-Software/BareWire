using System.Buffers;
using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BareWire.Abstractions.Transport;
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
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset staleCutoff = now - _options.OutboxLockTimeout;

        // Use the configured dialect's atomic claim only when it targets the active EF Core
        // provider — matched via the base DatabaseFacade.ProviderName API (keeps this package
        // provider-agnostic: no hard dependency on any provider package). Providers without a
        // matching dialect fall back to the non-atomic client-side claim below.
        if (string.Equals(_dbContext.Database.ProviderName, _dialect.ProviderName, StringComparison.Ordinal))
        {
            // Atomic disjoint claim via the provider dialect (PostgreSQL: FOR UPDATE SKIP LOCKED).
            // Deliberately two statements (claim UPDATE here, then the shared SELECT below) rather
            // than a single UPDATE ... RETURNING: EF Core's ExecuteSqlAsync returns only an
            // affected-row count, and FromSql/SqlQuery wrap the statement in a subquery — a
            // data-modifying statement cannot sit at non-top-level (e.g. on PostgreSQL) — so
            // RETURNING cannot be materialized through EF Core here. The follow-up SELECT
            // (LockedBy == this instance) reads back exactly the rows this instance just claimed.
            await _dbContext.Database.ExecuteSqlAsync(
                _dialect.GetClaimSql(_instanceId, now, staleCutoff, batchSize, _options.OrderingMode),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // SQLite / other providers: two-step non-concurrent claim.
            // Step 1: identify claimable ids — Take() in ExecuteUpdateAsync is not supported.
            // SQLite EF does not support nullable DateTimeOffset OR comparisons in a single Where.
            // Fetch pending (undelivered) ids, LockedAt, LockedBy, and OrderingKey client-side,
            // then filter in-memory. Rows already owned by this instance are always included
            // (refresh their lock timestamp).
            var pendingRows = await _dbContext.Set<OutboxMessage>()
                .Where(m => m.DeliveredAt == null)
                .Select(m => new { m.Id, m.LockedAt, m.LockedBy, m.OrderingKey })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Head-of-line filter for PerKey mode — applied BEFORE Take(batchSize) so the batch
            // limit is not consumed by blocked rows (O(n)/cycle over undelivered rows, where n is
            // the number of undelivered messages). This path is for SQLite / test/dev only —
            // NOT production. For production multi-instance deployments use PostgreSQL with the
            // atomic FOR UPDATE SKIP LOCKED claim path above.
            IEnumerable<long> claimableCandidates = pendingRows
                .Where(x => x.LockedBy == _instanceId
                    || x.LockedAt is null
                    || x.LockedAt.Value < staleCutoff)
                .OrderBy(x => x.Id)
                .Select(x => x.Id);

            if (_options.OrderingMode == BareWire.Abstractions.Outbox.OrderingMode.PerKey)
            {
                // Build a set of the minimum Id per key among all undelivered rows (not just
                // claimable ones) — these are the heads. A keyed row is claimable only when its
                // Id is the head Id for that key. Keyless rows (OrderingKey == null) always pass.
                Dictionary<string, long> headIdPerKey = pendingRows
                    .Where(x => x.OrderingKey is not null)
                    .GroupBy(x => x.OrderingKey!)
                    .ToDictionary(g => g.Key, g => g.Min(x => x.Id));

                claimableCandidates = claimableCandidates
                    .Where(id =>
                    {
                        var row = pendingRows.First(x => x.Id == id);
                        // Keyless rows are never blocked.
                        if (row.OrderingKey is null)
                        {
                            return true;
                        }

                        // A keyed row is claimable only if it is the head of its key group.
                        return headIdPerKey.TryGetValue(row.OrderingKey, out long headId)
                            && id == headId;
                    });
            }

            List<long> claimableIds = claimableCandidates
                .Take(batchSize)
                .ToList();

            if (claimableIds.Count == 0)
            {
                return Array.Empty<OutboxEntry>();
            }

            // Step 2: mark those specific ids as claimed.
            await _dbContext.Set<OutboxMessage>()
                .Where(m => claimableIds.Contains(m.Id))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(m => m.LockedAt, now)
                        .SetProperty(m => m.LockedBy, _instanceId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Select claimed rows: LockedBy == this instance AND not yet delivered.
        // Do NOT compare LockedAt == now — timestamptz precision truncation causes equality to fail.
        // Take(batchSize) bounds the returned batch: this instance may still own carry-forward rows
        // from a prior cycle (e.g. nacked rows whose lock has not yet expired), so without the cap a
        // cycle could return more than batchSize rows (and rent that many pooled buffers). Oldest-first
        // (OrderBy Id) ensures carry-forward rows drain before newer claims.
        List<OutboxMessage> rows = await _dbContext.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(m => m.LockedBy == _instanceId && m.DeliveredAt == null)
            .OrderBy(m => m.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
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
