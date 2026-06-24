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
    /// For example: <c>options => options.UseSqlServer(connectionString)</c>.
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

        // Register the background services that poll and dispatch pending outbox messages
        // and periodically clean up expired outbox/inbox records.
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<OutboxCleanupService>();

        // Build and register OutboxOptions as a singleton.
        // OutboxDispatcher and OutboxCleanupService resolve it directly from DI.
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

        // R7.7.7 — When PerKey ordering is active, register a startup checker that warns if the
        // active dialect does not override the 5-arg GetClaimSql (DIM passthrough silently disables
        // per-key head-of-line ordering). Registered only when needed — zero overhead for None mode.
        if (options.OrderingMode == OrderingMode.PerKey)
        {
            services.AddHostedService(sp => new OutboxDialectMismatchChecker(
                sp.GetRequiredService<IOutboxSqlDialect>(),
                sp.GetRequiredService<ILogger<OutboxDialectMismatchChecker>>()));
        }

        if (options.AutoCreateSchema)
        {
            services.AddHostedService<OutboxSchemaInitializer>();
        }

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
