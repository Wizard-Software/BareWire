namespace BareWire.Observability;

/// <summary>
/// Fluent configurator for BareWire observability, controlling whether OpenTelemetry
/// integration is enabled and how the OTLP exporter is wired up.
/// </summary>
/// <remarks>
/// Pass an <c>Action&lt;IObservabilityConfigurator&gt;</c> to
/// <c>AddBareWireObservability(configure)</c> to customise the defaults. When no
/// configure delegate is supplied, OpenTelemetry is enabled and the OTLP exporter reads
/// its endpoint from the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable.
/// </remarks>
public interface IObservabilityConfigurator
{
    /// <summary>
    /// Gets or sets a value indicating whether the OpenTelemetry SDK (tracing and metrics)
    /// is registered with the dependency injection container.
    /// </summary>
    /// <value>
    /// <see langword="true"/> by default. Set to <see langword="false"/> to skip all
    /// OpenTelemetry registrations and rely solely on the raw <c>BareWireActivitySource</c>
    /// and <c>BareWireMetrics</c> instrumentation classes.
    /// </value>
    bool EnableOpenTelemetry { get; set; }

    /// <summary>
    /// Configures the OTLP exporter so that traces and metrics are exported over the
    /// OpenTelemetry Protocol.
    /// </summary>
    /// <param name="endpoint">
    /// Optional base URI of the OTLP collector (e.g.
    /// <c>new Uri("http://localhost:4317")</c> for gRPC). When <see langword="null"/>,
    /// the endpoint is read from the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment
    /// variable at runtime.
    /// </param>
    /// <returns>The same <see cref="IObservabilityConfigurator"/> to allow method chaining.</returns>
    IObservabilityConfigurator UseOtlpExporter(Uri? endpoint = null);
}
