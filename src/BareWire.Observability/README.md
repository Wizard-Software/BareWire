# BareWire.Observability

OpenTelemetry integration for BareWire with traces, metrics, and health checks.

## Installation

```bash
dotnet add package BareWire.Observability
```

## Usage

```csharp
builder.AddBareWire(wire =>
{
    wire.UseOpenTelemetry(otel =>
    {
        otel.EnableTracing();
        otel.EnableMetrics();
        otel.EnableHealthChecks(threshold: 0.9);
    });
});
```

## Features

- Distributed tracing with W3C TraceContext propagation
- Publish/consume metrics (throughput, latency, inflight)
- Health checks with configurable alert thresholds (default 90%)
- OTLP exporter support

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
