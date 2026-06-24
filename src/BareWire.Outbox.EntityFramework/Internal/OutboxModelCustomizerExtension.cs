using BareWire.Outbox;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// An EF Core <see cref="IDbContextOptionsExtension"/> that registers
/// <see cref="OutboxModelCustomizer"/> into EF Core's internal service provider.
/// Adding this extension to <see cref="Microsoft.EntityFrameworkCore.DbContextOptionsBuilder"/> via
/// <see cref="IDbContextOptionsBuilderInfrastructure.AddOrUpdateExtension{TExtension}"/> is how
/// <see cref="OutboxModelCustomizer"/> receives the built <see cref="OutboxOptions"/> instance
/// without requiring a change to the <see cref="OutboxDbContext"/> constructor.
/// </summary>
internal sealed class OutboxModelCustomizerExtension : IDbContextOptionsExtension
{
    private readonly OutboxOptions _options;

    internal OutboxModelCustomizerExtension(OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        // Replace the default IModelCustomizer registered by the relational provider
        // with OutboxModelCustomizer. The customizer delegates to the base first, then
        // conditionally adds the IX_OutboxMessages_Ordering partial index when
        // OrderingMode == PerKey on a PostgreSQL provider.
        //
        // OutboxModelCustomizer wraps an IModelCustomizer that EF will resolve from its
        // own DI, so we register a factory that resolves the base via GetRequiredService
        // and then wraps it with our options.
        services.Replace(ServiceDescriptor.Singleton<IModelCustomizer>(sp =>
        {
            // Resolve the default relational customizer that was registered by the provider.
            // We use RelationalModelCustomizer directly to avoid an infinite loop — if we
            // resolved IModelCustomizer we would get back this registration.
            var deps = sp.GetRequiredService<ModelCustomizerDependencies>();
            var baseCustomizer = new RelationalModelCustomizer(deps);
            return new OutboxModelCustomizer(baseCustomizer, _options);
        }));
    }

    /// <inheritdoc />
    public void ApplyDefaults(IDbContextOptions options)
    {
        // OutboxOptions defaults are applied at build time in ServiceCollectionExtensions —
        // no dynamic defaults to apply here. Reference _options to satisfy CA1822.
        _ = _options;
    }

    /// <inheritdoc />
    public void Validate(IDbContextOptions options)
    {
        // OutboxOptions is validated eagerly in OutboxOptions.Validate() during OutboxConfigurator.Build().
        // Nothing further to validate at the EF Core level. Reference _options to satisfy CA1822.
        _ = _options;
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(IDbContextOptionsExtension extension) : base(extension) { }

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "outbox-ordering-customizer";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => true;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["BareWire:OutboxModelCustomizer"] = "1";
        }
    }
}
