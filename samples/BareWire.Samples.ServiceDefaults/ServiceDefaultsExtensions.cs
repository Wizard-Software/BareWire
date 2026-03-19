using BareWire.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BareWire.Samples.ServiceDefaults;

/// <summary>
/// Extension methods that wire up the shared defaults for every BareWire sample application:
/// OpenTelemetry observability (traces + metrics via OTLP) and health check endpoints.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// Registers BareWire observability (traces, metrics, OTLP export) and health checks
    /// on the application builder. Call this before <c>builder.Build()</c>.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // BareWire Observability: OTel tracing + metrics wired to the "BareWire" ActivitySource
        // and Meter. UseOtlpExporter() picks up OTEL_EXPORTER_OTLP_ENDPOINT automatically,
        // which Aspire Dashboard injects at runtime. When running without Aspire, set the env
        // var manually or omit it to suppress export.
        builder.Services.AddBareWireObservability(cfg =>
        {
            cfg.EnableOpenTelemetry = true;
            cfg.UseOtlpExporter();
        });

        // Standard health checks pipeline — individual checks are added by each sample
        // (e.g. AddNpgsql, AddRabbitMQ). MapServiceDefaults exposes the /health endpoints.
        builder.Services.AddHealthChecks();

        return builder;
    }

    /// <summary>
    /// Maps the standard health check endpoints on the application.
    /// Call this after <c>builder.Build()</c> and before <c>app.Run()</c>.
    /// </summary>
    /// <remarks>
    /// Endpoints registered:
    /// <list type="bullet">
    ///   <item><term>/health</term><description>Combined liveness + readiness check.</description></item>
    ///   <item><term>/health/live</term><description>Liveness only — no dependency checks (Predicate = _ =&gt; false).</description></item>
    ///   <item><term>/health/ready</term><description>Full readiness check including all registered checks.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="app">The web application.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static WebApplication MapServiceDefaults(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready");

        return app;
    }
}
