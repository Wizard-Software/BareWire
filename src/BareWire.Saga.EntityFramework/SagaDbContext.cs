using BareWire.Abstractions.Saga;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BareWire.Saga.EntityFramework;

/// <summary>
/// Abstraction for per-saga-type EF Core model configuration, registered via DI and
/// applied in <see cref="SagaDbContext.OnModelCreating"/>.
/// </summary>
internal interface ISagaModelConfiguration
{
    /// <summary>
    /// Returns the CLR type of the saga entity being configured.
    /// </summary>
    Type SagaType { get; }

    /// <summary>
    /// Applies the saga entity type configuration to the given <paramref name="modelBuilder"/>.
    /// </summary>
    void Configure(ModelBuilder modelBuilder);
}

/// <summary>
/// Applies <see cref="SagaEntityTypeConfiguration{TSaga}"/> for a specific saga type.
/// Registered as a singleton in DI by <see cref="ServiceCollectionExtensions.AddBareWireSaga{TSaga}"/>.
/// </summary>
internal sealed class SagaModelConfiguration<TSaga> : ISagaModelConfiguration
    where TSaga : class, ISagaState
{
    /// <inheritdoc />
    public Type SagaType => typeof(TSaga);

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfiguration(new SagaEntityTypeConfiguration<TSaga>());
}

/// <summary>
/// Custom model cache key factory that includes the set of registered saga types in the cache key.
/// This ensures that different saga type registrations produce different cached models,
/// preventing stale model reuse when multiple test classes configure different saga entities.
/// </summary>
internal sealed class SagaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        if (context is SagaDbContext sagaContext)
        {
            var typeKey = string.Join("|",
                sagaContext.ConfiguredSagaTypes
                    .OrderBy(t => t.FullName, StringComparer.Ordinal)
                    .Select(t => t.FullName));
            return (context.GetType(), typeKey, designTime);
        }

        return (context.GetType(), designTime);
    }
}

/// <summary>
/// DbContext for SAGA state persistence. Supports multiple saga types registered
/// via <see cref="ServiceCollectionExtensions.AddBareWireSaga{TSaga}"/>.
/// </summary>
/// <remarks>
/// This class is intentionally <c>public</c> and non-<c>sealed</c> to satisfy EF Core's
/// requirements for code-first migrations (<c>dotnet ef migrations add</c>) and the
/// <c>OnModelCreating</c> override pattern. All other implementation classes in this
/// assembly follow the <c>internal sealed</c> convention.
/// </remarks>
public class SagaDbContext : DbContext
{
    private readonly IEnumerable<ISagaModelConfiguration> _configurations;

    /// <summary>
    /// Initializes a new instance of <see cref="SagaDbContext"/>.
    /// </summary>
    /// <param name="options">The EF Core options configured for this context.</param>
    /// <param name="configurations">
    /// The per-saga-type model configurations registered via
    /// <see cref="ServiceCollectionExtensions.AddBareWireSaga{TSaga}"/>.
    /// </param>
    /// <remarks>
    /// The constructor is <c>internal</c> because <see cref="ISagaModelConfiguration"/> is an internal
    /// interface. Instances are created exclusively by the DI container.
    /// </remarks>
    internal SagaDbContext(
        DbContextOptions<SagaDbContext> options,
        IEnumerable<ISagaModelConfiguration> configurations)
        : base(options)
    {
        _configurations = configurations;
    }

    internal IEnumerable<Type> ConfiguredSagaTypes
        => _configurations.Select(c => c.SagaType);

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, SagaModelCacheKeyFactory>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var configuration in _configurations)
        {
            configuration.Configure(modelBuilder);
        }
    }
}
