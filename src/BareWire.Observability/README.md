# BareWire.Observability

OpenTelemetry integration for BareWire with traces, metrics, and health checks.

## Installation

```bash
dotnet add package BareWire.Observability
```

## Usage

```csharp
builder.Services.AddBareWireObservability(otel =>
{
    otel.UseOtlpExporter(); // endpoint from OTEL_EXPORTER_OTLP_ENDPOINT when omitted
});
```

## Features

- Distributed tracing with W3C TraceContext propagation
- Publish/consume metrics (throughput, latency, inflight)
- Health checks with configurable alert thresholds (default 90%)
- OTLP exporter support

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
