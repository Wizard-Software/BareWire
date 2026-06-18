using BareWire.Abstractions.Saga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

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
    /// before calling this method. Use <see cref="AddBareWireRedisConnection"/> to configure and
    /// register the connection with TLS, Sentinel, and Cluster support.
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

    /// <summary>
    /// Configures and registers a StackExchange.Redis <see cref="IConnectionMultiplexer"/> as a
    /// singleton in the DI container using <see cref="RedisConnectionOptions"/>.
    /// </summary>
    /// <param name="services">The service collection to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the <see cref="RedisConnectionOptions"/> for the Redis connection.
    /// For example: <c>opts => { opts.Endpoints.Add("localhost:6379"); opts.Ssl = true; }</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="BareWire.Abstractions.Exceptions.BareWireConfigurationException">
    /// Thrown eagerly at call time (not at first resolve) when the options are invalid —
    /// for example, when no endpoints are provided or when TLS is required but disabled.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The <see cref="IConnectionMultiplexer"/> is registered with <c>TryAddSingleton</c> semantics,
    /// so calling this method twice does not add a second descriptor. If an
    /// <see cref="IConnectionMultiplexer"/> was already registered by the application, this method
    /// leaves the existing registration in place.
    /// </para>
    /// <para>
    /// The connection is established synchronously inside the DI factory using
    /// <c>ConnectionMultiplexer.Connect</c>. Because <see cref="RedisConnectionOptions.AbortOnConnectFail"/>
    /// defaults to <see langword="false"/>, the factory returns quickly even when Redis is temporarily
    /// unavailable — StackExchange.Redis reconnects in the background.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBareWireRedisConnection(
        this IServiceCollection services,
        Action<RedisConnectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RedisConnectionOptions();
        configure(options);

        // Build and validate options eagerly so misconfiguration throws at startup, not at first resolve.
        var config = RedisConfigurationBuilder.Build(options);

        services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(config));

        return services;
    }
}
