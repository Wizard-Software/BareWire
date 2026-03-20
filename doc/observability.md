# Observability

BareWire integrates with OpenTelemetry for distributed tracing, metrics, and health checks.

## Enabling Observability

```csharp
builder.Services.AddBareWireObservability(cfg =>
{
    cfg.EnableOpenTelemetry = true;
});
```

## Distributed Tracing

BareWire propagates trace context across message boundaries. Each publish and consume operation creates a span, so you get end-to-end traces across multi-hop workflows.

For example, in the ObservabilityShowcase sample, a single request produces a 3-hop trace:

```
POST /demo/run
  → DemoOrderCreated (order.created)
    → DemoPaymentProcessed (payment.processed)
      → DemoShipmentDispatched (shipment.dispatched)
```

Each hop is visible as a child span in your tracing backend (Aspire Dashboard, Jaeger, Zipkin, etc.).

## Metrics

BareWire emits the following metrics via OpenTelemetry:

| Metric | Description |
|---|---|
| `barewire.messages.published` | Counter of published messages |
| `barewire.messages.consumed` | Counter of consumed messages |
| `barewire.message.duration` | Histogram of message processing duration |

## Health Checks

BareWire registers health checks that report transport and flow control status:

```csharp
builder.AddServiceDefaults();  // registers health checks
// ...
app.MapServiceDefaults();      // maps health endpoints
```

Endpoints:
- `/health` — combined liveness + readiness
- `/health/live` — liveness only (no dependency checks)
- `/health/ready` — full readiness including broker connectivity and flow control

## Aspire Dashboard

When running via the Aspire AppHost, traces and metrics are automatically exported to the Aspire Dashboard. No additional configuration is needed — the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable is injected automatically.

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

## Combining with SAGA and Outbox

Observability works seamlessly with SAGA and outbox. The RabbitMQ sample demonstrates all three together:

```csharp
builder.Services.AddBareWire(cfg => { /* transport */ });
builder.Services.AddBareWireSaga<OrderSagaState>(options => options.UseSqlite(...));
builder.Services.AddBareWireOutbox(configureDbContext, configureOutbox);
builder.Services.AddBareWireObservability(cfg => { cfg.EnableOpenTelemetry = true; });
```

> See: `samples/BareWire.Samples.ObservabilityShowcase/` and `samples/BareWire.Samples.RabbitMQ/`
