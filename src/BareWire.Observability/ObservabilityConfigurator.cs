namespace BareWire.Observability;

internal sealed class ObservabilityConfigurator : IObservabilityConfigurator
{
    public bool EnableOpenTelemetry { get; set; } = true;

    internal Uri? OtlpEndpoint { get; private set; }

    internal bool OtlpConfigured { get; private set; }

    public IObservabilityConfigurator UseOtlpExporter(Uri? endpoint = null)
    {
        OtlpEndpoint = endpoint;
        OtlpConfigured = true;
        return this;
    }
}
