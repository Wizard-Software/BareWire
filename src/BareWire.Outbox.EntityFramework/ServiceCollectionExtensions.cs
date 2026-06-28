using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Pipeline;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// Extension methods for registering the BareWire transactional outbox/inbox
/// persistence layer using Entity Framework Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BareWire transactional outbox and inbox services backed by
    /// Entity Framework Core for reliable, at-least-once message delivery.
    /// </summary>
    /// <param name="services">The service collection to register services into.</param>
    /// <param name="configureDbContext">
    /// A delegate that configures the <see cref="DbContextOptionsBuilder"/> for
    /// <see cref="OutboxDbContext"/>.
    /// For example: <c>options => options.UseNpgsql(connectionString)</c>.
    /// Providers without a matching atomic dialect require a custom <see cref="IOutboxSqlDialect"/>
    /// and <see cref="IInboxSqlDialect"/>, or <see cref="IOutboxConfigurator.AllowNonAtomicProvider"/>
    /// must be set to <see langword="true"/> for single-instance / testing scenarios.
    /// </param>
    /// <param name="configureOutbox">
    /// An optional delegate for customizing outbox behavior such as polling interval,
    /// batch size, and retention periods. When <see langword="null"/>, defaults are used.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configureDbContext"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="BareWire.Abstractions.Exceptions.BareWireConfigurationException">
    /// Thrown when the outbox configuration supplied via <paramref name="configureOutbox"/>
    /// contains invalid values (e.g. non-positive intervals, out-of-range batch size).
    /// </exception>
    public static IServiceCollection AddBareWireOutbox(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        Action<IOutboxConfigurator>? configureOutbox = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        // Generate a stable per-process instance identifier — unique across restarts and hosts.
        // MachineName:PID ensures readability; Guid suffix prevents PID-reuse collisions.
        var instanceId = new OutboxInstanceId(
            $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");

        services.AddSingleton(instanceId);

        // Exposes the connection the transactional middleware pins per consume operation so a consumer
        // DbContext can share it (single-phase commit instead of a two-phase prepared commit). Stateless
        // singleton over an async-flow-local — see IOutboxConnectionAccessor for the consumer wiring.
        services.TryAddSingleton<IOutboxConnectionAccessor, OutboxConnectionAccessor>();

        // Default outbox claim dialect: PostgreSQL (FOR UPDATE SKIP LOCKED). The store invokes a
        // dialect only when its IOutboxSqlDialect.ProviderName matches the active EF Core provider,
        // so this default is used on PostgreSQL and is inert elsewhere. To get an atomic claim on
        // another provider (e.g. SQL Server), register a custom IOutboxSqlDialect with the matching
        // ProviderName BEFORE calling AddBareWireOutbox (TryAdd keeps your registration). Providers
        // without a matching dialect use a non-atomic client-side fallback (single-instance/testing).
        services.TryAddSingleton<IOutboxSqlDialect, PostgresOutboxSqlDialect>();

        // Register the EF Core store implementations as scoped — they depend on the
        // scoped OutboxDbContext and must not outlive it.
        // Factory lambdas are required because the implementation classes have internal constructors.
        services.AddScoped<IOutboxStore>(sp =>
            new EfCoreOutboxStore(
                sp.GetRequiredService<OutboxDbContext>(),
                sp.GetRequiredService<OutboxInstanceId>(),
                sp.GetRequiredService<IOutboxSqlDialect>(),
                sp.GetRequiredService<OutboxOptions>()));

        // Register the default SQL dialect for inbox upserts (PostgreSQL).
        // Users can replace this with a custom implementation for other database providers.
        services.TryAddSingleton<IInboxSqlDialect, PostgresInboxSqlDialect>();
        services.AddScoped<IInboxStore>(sp =>
            new EfCoreInboxStore(
                sp.GetRequiredService<OutboxDbContext>(),
                sp.GetRequiredService<IInboxSqlDialect>()));

        // Register InboxFilter as scoped — it depends on the scoped IInboxStore.
        services.AddScoped(sp => new InboxFilter(
            sp.GetRequiredService<IInboxStore>(),
            sp.GetRequiredService<OutboxOptions>(),
            sp.GetRequiredService<ILogger<InboxFilter>>()));

        // Register the transactional middleware as scoped (depends on OutboxDbContext + EfCoreInboxStore).
        services.AddScoped<IMessageMiddleware>(sp => new TransactionalOutboxMiddleware(
            sp.GetRequiredService<OutboxDbContext>(),
            sp.GetRequiredService<IOutboxStore>(),
            sp.GetRequiredService<InboxFilter>(),
            sp.GetRequiredService<ILogger<TransactionalOutboxMiddleware>>()));

        // Build and register OutboxOptions as a singleton BEFORE the hosted services, so their
        // registration can branch on it (AutoCreateSchema, OrderingMode) and the start order below is
        // explicit. OutboxDispatcher and OutboxCleanupService resolve it directly from DI.
        OutboxOptions options;

        if (configureOutbox is not null)
        {
            var configurator = new OutboxConfigurator();
            configureOutbox(configurator);
            options = configurator.Build(); // validates and throws BareWireConfigurationException on bad values
        }
        else
        {
            options = OutboxOptions.Default;
        }

        services.AddSingleton(options);

        // Hosted-service registration order is load-bearing: IHostedService.StartAsync runs sequentially
        // in registration order, each awaited before the next starts. The dispatcher gates its first
        // claim/send on IHostApplicationLifetime.ApplicationStarted, which fires only after every service
        // below has started successfully — so the guards run (and can fail fast) before any outbox side
        // effect. The sequence is:
        //   1. provider-atomicity guard — fail fast on a non-atomic provider;
        //   2. per-key ordering guard (when PerKey) — fail fast on a dialect without head-of-line support;
        //   3. schema initializer (when AutoCreateSchema) — create the outbox/inbox tables;
        //   4. dispatcher — claims/sends nothing until ApplicationStarted (i.e. until every guard passed);
        //   5. cleanup service.

        // 1. Provider-atomicity guard, first so it fails fast before schema creation or dispatch. It
        // resolves OutboxOptions lazily at StartAsync time; the singleton registered just above is
        // available because all DI registrations complete before the host calls StartAsync.
        services.AddHostedService(sp => new OutboxProviderAtomicityChecker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOutboxSqlDialect>(),
            sp.GetRequiredService<IInboxSqlDialect>(),
            sp.GetRequiredService<OutboxOptions>(),
            sp.GetRequiredService<ILogger<OutboxProviderAtomicityChecker>>()));

        // 2. Per-key ordering guard — registered BEFORE the dispatcher so a PerKey configuration on a
        // dialect that lacks native head-of-line ordering fails fast (or warns under AllowDegradedOrdering)
        // before any batch is claimed and irreversibly delivered out of order. Registered only when needed
        // — zero overhead for None mode.
        if (options.OrderingMode == OrderingMode.PerKey)
        {
            services.AddHostedService(sp => new OutboxDialectMismatchChecker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOutboxSqlDialect>(),
                sp.GetRequiredService<OutboxOptions>(),
                sp.GetRequiredService<ILogger<OutboxDialectMismatchChecker>>()));
        }

        // 3. Schema initializer — registered BEFORE the dispatcher so the outbox/inbox tables exist
        // before the dispatcher's first poll. Only when AutoCreateSchema is enabled; otherwise the
        // operator owns schema creation (migrations) and no race exists.
        if (options.AutoCreateSchema)
        {
            services.AddHostedService<OutboxSchemaInitializer>();
        }

        // 4 & 5. Background services that poll and dispatch pending outbox messages (the dispatcher gates
        // its loop on IHostApplicationLifetime.ApplicationStarted), and periodically clean up expired rows.
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<OutboxCleanupService>();

        // Register the EF Core DbContext together with the ordering model customizer extension.
        // OutboxModelCustomizerExtension.ApplyServices registers OutboxModelCustomizer into EF
        // Core's internal service provider, which conditionally adds the partial index
        // IX_OutboxMessages_Ordering when OrderingMode == PerKey on a PostgreSQL provider.
        // OutboxOptions is captured in the closure above — no static state, no changes to
        // OutboxDbContext's constructor (variant B of OQ-1, R7.7 plan §2.9).
        var customizationExtension = new OutboxModelCustomizerExtension(options);
        services.AddDbContext<OutboxDbContext>((_, ob) =>
        {
            configureDbContext(ob);
            ((IDbContextOptionsBuilderInfrastructure)ob).AddOrUpdateExtension(customizationExtension);
        });

        return services;
    }
}
