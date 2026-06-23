using System.Buffers;
using System.Collections.Frozen;
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

    internal EfCoreOutboxStore(
        OutboxDbContext dbContext,
        OutboxInstanceId instanceId,
        IOutboxSqlDialect dialect,
        OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);

        _dbContext = dbContext;
        _instanceId = instanceId.Value;
        _dialect = dialect;
        _options = options;
    }

    public ValueTask SaveMessagesAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

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
                CreatedAt = now
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
        DateTimeOffset now = DateTimeOffset.UtcNow;
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
                _dialect.GetClaimSql(_instanceId, now, staleCutoff, batchSize),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // SQLite / other providers: two-step non-concurrent claim.
            // Step 1: identify claimable ids — Take() in ExecuteUpdateAsync is not supported.
            // SQLite EF does not support nullable DateTimeOffset OR comparisons in a single Where.
            // Fetch pending (undelivered) ids, LockedAt, and LockedBy client-side, then filter in-memory.
            // Rows already owned by this instance are always included (refresh their lock timestamp).
            var pendingRows = await _dbContext.Set<OutboxMessage>()
                .Where(m => m.DeliveredAt == null)
                .Select(m => new { m.Id, m.LockedAt, m.LockedBy })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            List<long> claimableIds = pendingRows
                .Where(x => x.LockedBy == _instanceId
                    || x.LockedAt is null
                    || x.LockedAt.Value < staleCutoff)
                .OrderBy(x => x.Id)
                .Take(batchSize)
                .Select(x => x.Id)
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
                Status = OutboxEntryStatus.Pending
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

        await _dbContext.Set<OutboxMessage>()
            .Where(m => ids.Contains(m.Id))
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.DeliveredAt, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return FrozenSet<long>.Empty;
        }

        // Zero the lock on rows this instance still owns and has not yet delivered, so the next
        // poll cycle re-claims them immediately. The LockedBy == _instanceId filter ensures an
        // instance never releases another instance's claim (preserves the B4 no-double-delivery
        // guarantee); DeliveredAt == null guards against a race with MarkDeliveredAsync.
        await _dbContext.Set<OutboxMessage>()
            .Where(m => ids.Contains(m.Id) && m.LockedBy == _instanceId && m.DeliveredAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.LockedAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedBy, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);

        // GetPendingAsync copies each row into a fresh per-cycle pooled buffer, so this store
        // retains no caller buffers — the dispatcher must return all of them.
        return FrozenSet<long>.Empty;
    }

    public async ValueTask CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - retention;

        await _dbContext.Set<OutboxMessage>()
            .Where(m => m.DeliveredAt != null && m.DeliveredAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
