using BareWire.Abstractions.Saga;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.Saga.EntityFramework;

/// <summary>
/// Extension methods for registering BareWire SAGA persistence with Entity Framework Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SAGA state persistence layer using Entity Framework Core.
    /// </summary>
    /// <typeparam name="TSaga">The saga state type to persist. Must implement <see cref="ISagaState"/>.</typeparam>
    /// <param name="services">The service collection to register services into.</param>
    /// <param name="configureDbContext">
    /// A delegate that configures the <see cref="DbContextOptionsBuilder"/> for <see cref="SagaDbContext"/>.
    /// For example: <c>options => options.UseSqlServer(connectionString)</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configureDbContext"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddBareWireSaga<TSaga>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext)
        where TSaga : class, ISagaState
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        // Register the per-saga model configuration. Multiple calls for different TSaga types
        // each register their own ISagaModelConfiguration, all resolved by SagaDbContext.
        services.AddSingleton<ISagaModelConfiguration, SagaModelConfiguration<TSaga>>();

        // Register SagaDbContext once. AddDbContext is idempotent for the same TContext type
        // when called multiple times — the last configureDbContext wins for the provider setup,
        // but ISagaModelConfiguration registrations accumulate correctly.
        services.AddDbContext<SagaDbContext>(configureDbContext);

        // Register the EF Core repository implementations for this saga type.
        services.AddScoped<ISagaRepository<TSaga>, EfCoreSagaRepository<TSaga>>();
        services.AddScoped<IQueryableSagaRepository<TSaga>, EfCoreSagaRepository<TSaga>>();

        return services;
    }
}
