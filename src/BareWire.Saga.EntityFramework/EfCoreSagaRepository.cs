using System.Linq.Expressions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using Microsoft.EntityFrameworkCore;

namespace BareWire.Saga.EntityFramework;

internal sealed class EfCoreSagaRepository<TSaga> : IQueryableSagaRepository<TSaga>
    where TSaga : class, ISagaState
{
    private readonly SagaDbContext _dbContext;

    public EfCoreSagaRepository(SagaDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TSaga?> FindAsync(Guid correlationId, CancellationToken cancellationToken = default)
        => await _dbContext.Set<TSaga>().FindAsync([correlationId], cancellationToken).ConfigureAwait(false);

    public async Task SaveAsync(TSaga saga, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saga);

        _dbContext.Set<TSaga>().Add(saga);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            throw new InvalidOperationException(
                $"A saga of type '{typeof(TSaga).Name}' with CorrelationId '{saga.CorrelationId}' already exists.",
                ex);
        }
    }

    public async Task UpdateAsync(TSaga saga, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saga);

        int expectedVersion = saga.Version;
        saga.Version++;

        var entry = _dbContext.Entry(saga);
        if (entry.State == EntityState.Detached)
        {
            _dbContext.Set<TSaga>().Attach(saga);
            entry.State = EntityState.Modified;
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            saga.Version = expectedVersion;
            throw new ConcurrencyException(
                typeof(TSaga),
                saga.CorrelationId,
                expectedVersion,
                actualVersion: -1,
                saga.CurrentState,
                ex);
        }
    }

    public async Task DeleteAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var saga = await _dbContext.Set<TSaga>().FindAsync([correlationId], cancellationToken).ConfigureAwait(false);

        if (saga is not null)
        {
            _dbContext.Set<TSaga>().Remove(saga);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TSaga?> FindSingleAsync(
        Expression<Func<TSaga, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return await _dbContext.Set<TSaga>()
            .Where(predicate)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase) == true;
}
