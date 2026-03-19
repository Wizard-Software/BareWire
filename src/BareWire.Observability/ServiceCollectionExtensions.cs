using BareWire.Abstractions.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BareWire.Observability;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering
/// BareWire observability services (tracing and metrics) with the .NET dependency injection container.
/// </summary>
/// <remarks>
/// This class is <see langword="public"/> and <see langword="static"/> because it contains
/// extension methods — an explicit exception to the <c>internal</c> visibility rule that
/// applies to all other implementation classes in <c>BareWire.Observability</c>.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers BareWire observability services (OpenTelemetry tracing and metrics) with the
    /// dependency injection container, replacing the no-op default registered by
    /// <c>AddBareWire()</c>. Delegates to <see cref="AddBareWireObservability(IServiceCollection, Action{IObservabilityConfigurator}?)"/>
    /// with no additional configuration.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddBareWireObservability(this IServiceCollection services)
        => AddBareWireObservability(services, configure: null);

    /// <summary>
    /// Registers BareWire observability services (OpenTelemetry tracing and metrics) with the
    /// dependency injection container, replacing the no-op default registered by
    /// <c>AddBareWire()</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// Optional delegate to customise observability behaviour via <see cref="IObservabilityConfigurator"/>.
    /// When <see langword="null"/>, OpenTelemetry is enabled and the OTLP exporter is <em>not</em>
    /// automatically registered — the host application is expected to call
    /// <c>UseOtlpExporter()</c> on its own <c>IOpenTelemetryBuilder</c>, or set
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> via environment variables.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Call this method after <c>AddBareWire()</c> to enable real observability. Without it,
    /// <c>AddBareWire()</c> registers <c>NullInstrumentation</c> as the fallback, which silently
    /// discards all tracing and metrics calls.
    /// </para>
    /// <para>
    /// <c>BareWireMetrics</c> is registered as a singleton so that <c>IMeterFactory</c> (provided
    /// by the host) is used to create the <c>Meter</c> instance, ensuring proper lifecycle
    /// management and testability via <c>IMeterFactory</c> fakes.
    /// </para>
    /// <para>
    /// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace"/>
    /// is used instead of <c>TryAddSingleton</c> to guarantee that the real implementation
    /// overrides the no-op default regardless of registration order.
    /// </para>
    /// <para>
    /// When <see cref="IObservabilityConfigurator.EnableOpenTelemetry"/> is <see langword="true"/>
    /// (the default), the BareWire <c>ActivitySource</c> and <c>Meter</c> (both named
    /// <c>"BareWire"</c>) are registered with the OpenTelemetry SDK. If
    /// <see cref="IObservabilityConfigurator.UseOtlpExporter"/> was also called, the OTLP
    /// exporter is wired up using <c>OtlpExportProtocol.Grpc</c> with the supplied endpoint,
    /// or with no explicit endpoint (env-var driven) when <see langword="null"/> was passed.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBareWireObservability(
        this IServiceCollection services,
        Action<IObservabilityConfigurator>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configurator = new ObservabilityConfigurator();
        configure?.Invoke(configurator);

        // BareWireMetrics wraps IMeterFactory (supplied by the host) to create the "BareWire" Meter.
        // Registered as Singleton so instrument instances are shared across all callers.
        services.TryAddSingleton<BareWireMetrics>();

        // Replace the NullInstrumentation default (registered by AddBareWire via TryAddSingleton)
        // with the real implementation that delegates to BareWireActivitySource, BareWireMetrics,
        // and TraceContextPropagator.
        services.Replace(ServiceDescriptor.Singleton<IBareWireInstrumentation, BareWireInstrumentation>());

        // Register the bus health check so the standard /health endpoint reflects BareWire status.
        // Reports bus + endpoint status only — no connection strings or secrets (SEC-06).
        services.AddHealthChecks()
            .AddCheck<BareWireHealthCheck>("barewire", tags: ["barewire", "messaging"]);

        if (configurator.EnableOpenTelemetry)
        {
            services.AddOpenTelemetry()
                .WithTracing(builder => builder.AddSource("BareWire"));

            services.AddOpenTelemetry()
                .WithMetrics(builder => builder.AddMeter("BareWire"));

            if (configurator.OtlpConfigured)
            {
                if (configurator.OtlpEndpoint is not null)
                {
                    services.AddOpenTelemetry()
                        .UseOtlpExporter(OtlpExportProtocol.Grpc, configurator.OtlpEndpoint);
                }
                else
                {
                    services.AddOpenTelemetry()
                        .UseOtlpExporter();
                }
            }
        }

        return services;
    }
}
