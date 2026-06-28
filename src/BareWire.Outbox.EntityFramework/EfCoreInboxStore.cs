using Microsoft.EntityFrameworkCore;

namespace BareWire.Outbox.EntityFramework;

internal sealed class EfCoreInboxStore : IInboxStore
{
    private readonly OutboxDbContext _dbContext;
    private readonly IInboxSqlDialect _dialect;

    internal EfCoreInboxStore(OutboxDbContext dbContext, IInboxSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(dialect);
        _dbContext = dbContext;
        _dialect = dialect;
    }

    public async ValueTask<bool> TryLockAsync(
        Guid messageId,
        string consumerType,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumerType);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now + lockTimeout;

        // Upsert via dialect — eliminates DbUpdateException on duplicates.
        int rowsAffected = await _dbContext.Database.ExecuteSqlAsync(
            _dialect.GetUpsertSql(messageId, consumerType, now, expiresAt),
            cancellationToken).ConfigureAwait(false);

        if (rowsAffected > 0)
        {
            return true; // Lock acquired (fresh insert).
        }

        // Existing entry — re-acquire ONLY if it is expired AND unprocessed.
        //
        // Use the configured dialect's atomic re-lock SQL when it targets the active EF Core provider
        // — matched via the base DatabaseFacade.ProviderName API (keeps this package
        // provider-agnostic: no hard dependency on any provider package, and any provider with a
        // matching dialect gets the atomic path). A single conditional UPDATE makes the re-lock
        // atomic: concurrent workers racing on the same expired row contend on the row lock, so
        // exactly one UPDATE matches (reLockedRows == 1) and the losers match zero rows — which
        // upholds the inbox dedup guarantee. Returning reLockedRows == 1, rather than an
        // unconditional update that always returns true, is what closes the double-processing window.
        // Mirrors InMemoryInboxStore's compare-and-swap re-lock and EfCoreOutboxStore's provider-gated
        // atomic claim.
        if (string.Equals(
                _dbContext.Database.ProviderName,
                _dialect.ProviderName,
                StringComparison.Ordinal))
        {
            int reLockedRows = await _dbContext.Database.ExecuteSqlAsync(
                _dialect.GetReLockSql(messageId, consumerType, now, expiresAt),
                cancellationToken).ConfigureAwait(false);

            return reLockedRows == 1;
        }

        // Providers without a matching dialect (e.g. SQLite for tests, or a provider whose dialect
        // was not registered): non-atomic read-then-update fallback. Safe for a single dispatcher
        // instance / testing; multi-instance production must run on a provider whose dialect's
        // ProviderName matches so the atomic re-lock above is used. Consistent with the outbox
        // store's client-side claim fallback for unmatched providers.
        InboxMessage? existing = await _dbContext.Set<InboxMessage>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.MessageId == messageId && m.ConsumerType == consumerType,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null || existing.ExpiresAt >= now || existing.ProcessedAt is not null)
        {
            return false; // Not expired, already processed, or missing — duplicate.
        }

        await _dbContext.Set<InboxMessage>()
            .Where(m => m.MessageId == messageId && m.ConsumerType == consumerType)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.ExpiresAt, expiresAt),
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    public async ValueTask MarkProcessedAsync(
        Guid messageId,
        string consumerType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumerType);

        await _dbContext.Set<InboxMessage>()
            .Where(m => m.MessageId == messageId && m.ConsumerType == consumerType)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.ProcessedAt, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - retention;

        await _dbContext.Set<InboxMessage>()
            .Where(m => m.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
