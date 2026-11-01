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

Publish spans are created for `PublishAsync`, `ISendEndpoint.SendAsync`, `SendRawAsync`, `PublishRawAsync` and the RabbitMQ request client, and the W3C trace context (`traceparent`, `tracestate`, `baggage`) is injected into the outgoing message headers. The span's destination tag contains only the scheme, host and path of the address — never credentials or query strings — and the request client span does not carry the request correlation id.

On the consume side, BareWire reads `traceparent` and falls back to `MT-Activity-Id`, the header MassTransit uses, when `traceparent` is absent; `traceparent` wins when both are present. Messages serialized with the MassTransit envelope also carry `MT-Activity-Id`, so a trace continues across BareWire and MassTransit services. See [MassTransit Interop](masstransit-interop.md#distributed-tracing) for details and limits.

## Metrics

BareWire emits the following metrics via OpenTelemetry:

| Metric | Description |
|---|---|
| `barewire.messages.published` | Counter of published messages |
| `barewire.messages.consumed` | Counter of consumed messages |
| `barewire.publish.intercepted` | Counter of outbound messages captured by an outbound message interceptor (for example the transactional outbox buffering a message published from a consumer). Captured messages are not counted in `barewire.messages.published` |
| `barewire.message.duration` | Histogram of message processing duration |

Feature packages add their own instruments on the same `BareWire` meter — for example
`barewire.outbox.rows.retried` and `barewire.outbox.retry.oldest_due_age` (see [Outbox](outbox.md)) and
`barewire.inbox.duplicates` (see [Inbox](inbox.md)).

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

A transport can report its own endpoints too: the in-memory transport reports queues at or above 90% of
capacity, and RabbitMQ reports consumers that have been waiting for automatic connection recovery for too
long (see [Connection recovery and health](transport-rabbitmq.md#connection-recovery-and-health)). Both
appear as `Degraded`.

The bus health description starts with a summary followed by the transport's own description, when the
transport reports trouble. The summary mentions capacity only when flow control (in-flight limits) causes
the worst status — `One or more endpoints are approaching capacity.` (`Degraded`) or
`One or more endpoints are at capacity.` (`Unhealthy`). When only the transport reports the worst status,
the summary is neutral — `One or more endpoints are degraded.` or `One or more endpoints are unhealthy.` —
for example `One or more endpoints are degraded. 1 RabbitMQ consumer(s) waiting for automatic recovery for at least 30 s.`

## Aspire Dashboard

When running via the Aspire AppHost, traces and metrics are automatically exported to the Aspire Dashboard. No additional configuration is needed — the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable is injected automatically.

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

## Combining with SAGA and Outbox

Observability works seamlessly with SAGA and outbox. The RabbitMQ sample demonstrates all three together:

```csharp
builder.Services.AddBareWireWithRabbitMq(transport => { /* host, topology, endpoints */ });
builder.Services.AddBareWireSaga<OrderSagaState>(options => options.UseSqlite(...));
builder.Services.AddBareWireOutbox(configureDbContext, configureOutbox);
builder.Services.AddBareWireObservability(cfg => { cfg.EnableOpenTelemetry = true; });
```

> See: `samples/BareWire.Samples.ObservabilityShowcase/` and `samples/BareWire.Samples.RabbitMQ/`
