using BareWire.Abstractions.Saga;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.Saga.Redis;

/// <summary>
/// Extension methods for registering BareWire SAGA persistence with Redis.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SAGA state persistence layer using Redis via StackExchange.Redis.
    /// </summary>
    /// <typeparam name="TSaga">The saga state type to persist. Must implement <see cref="ISagaState"/>.</typeparam>
    /// <param name="services">The service collection to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures <see cref="RedisSagaRepositoryOptions"/> for this SAGA type.
    /// For example: <c>options => options.StateTtl = TimeSpan.FromHours(24)</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method does NOT register <c>IConnectionMultiplexer</c>. The caller is responsible
    /// for registering a StackExchange.Redis <c>IConnectionMultiplexer</c> singleton separately
    /// before calling this method (full connection configuration including TLS, Sentinel, and
    /// Cluster support is provided by R6.2).
    /// </para>
    /// <para>
    /// The <see cref="ISagaRepository{TSaga}"/> is registered with a scoped lifetime.
    /// <c>IQueryableSagaRepository</c> is intentionally not registered — Redis supports
    /// identity-only access (lookup by <see cref="ISagaState.CorrelationId"/>), not arbitrary queries.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBareWireSagaRedis<TSaga>(
        this IServiceCollection services,
        Action<RedisSagaRepositoryOptions> configure)
        where TSaga : class, ISagaState
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RedisSagaRepositoryOptions
        {
            KeyPrefix = typeof(TSaga).Name
        };
        configure(options);

        services.AddSingleton(options);
        services.AddSingleton<SagaStateSerializer<TSaga>>();
        services.AddScoped<ISagaRepository<TSaga>, RedisSagaRepository<TSaga>>();

        return services;
    }
}
