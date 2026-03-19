// BareWire.Samples.BackpressureDemo — demonstrates ADR-004 and ADR-006 back-pressure.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: exchange and queue declared explicitly.
//   - ADR-004  Credit-based flow control (consume side):
//                FlowControlOptions.MaxInFlightMessages = 50
//                FlowControlOptions.MaxInFlightBytes    = 1 MiB
//              The framework tracks in-flight messages and stops prefetching when the
//              limit is reached, letting the broker buffer undelivered messages safely.
//   - ADR-006  Publish-side back-pressure:
//                PublishFlowControlOptions.MaxPendingPublishes = 500
//              When the outgoing channel is full, PublishAsync awaits — the LoadGenerator
//              loop naturally slows to match consumer throughput.
//   - SlowConsumer: 100 ms delay per message (10 msg/s peak throughput at ConcurrentLimit=8).
//   - LoadGenerator: configurable publish rate (default 1 000 msg/s), starts/stops via API.
//   - ServiceDefaults: OpenTelemetry observability + health checks.
//
// Architecture:
//   POST /load-test/start → LoadGenerator (BackgroundService)
//       └→ publishes LoadTestMessage at 1 000 msg/s
//            └→ SlowConsumer (queue: "loadtest-processing", 100 ms/msg)
//                  → ADR-006 back-pressure slows publisher
//                  → ADR-004 flow control stops prefetch at MaxInFlightMessages
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   When running via Aspire AppHost, the broker is provisioned automatically.

using BareWire.Abstractions;
using BareWire.Core;
using BareWire.Observability;
using BareWire.Samples.BackpressureDemo.Consumers;
using BareWire.Samples.BackpressureDemo.Messages;
using BareWire.Samples.BackpressureDemo.Services;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Shared defaults: OpenTelemetry observability + health checks
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Configuration
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

// ─────────────────────────────────────────────────────────────────────────────
// 3. Publish-side back-pressure (ADR-006)
// ─────────────────────────────────────────────────────────────────────────────

// Bounded outgoing channel: PublishAsync blocks when 500 publishes are pending.
// Register before AddBareWire so that AddBareWire's TryAdd is skipped.
builder.Services.AddSingleton(new PublishFlowControlOptions
{
    MaxPendingPublishes = 500,
});

// ─────────────────────────────────────────────────────────────────────────────
// 4. Consume-side flow control (ADR-004)
// ─────────────────────────────────────────────────────────────────────────────

// Credit-based: the framework stops prefetching once 50 messages are in-flight
// or the aggregate body size exceeds 1 MiB. Health check reports Degraded at 90%.
builder.Services.AddSingleton(new FlowControlOptions
{
    MaxInFlightMessages = 50,
    MaxInFlightBytes    = 1_048_576, // 1 MiB
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
// No envelope wrapper added by default.
builder.Services.AddBareWireJsonSerializer();

// Register the consumer in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<SlowConsumer>();

builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(rmq =>
    {
        // Connection to the RabbitMQ broker.
        rmq.Host(rabbitMqConnectionString);

        // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
        // The broker resources are deployed by IBusControl.DeployTopologyAsync on startup.
        rmq.ConfigureTopology(t =>
        {
            // Fanout exchange — every LoadTestMessage published is delivered to
            // the "loadtest-processing" queue.
            t.DeclareExchange("loadtest", ExchangeType.Fanout, durable: true);

            t.DeclareQueue("loadtest-processing", durable: true);
            t.BindExchangeToQueue("loadtest", "loadtest-processing", routingKey: "#");
        });

        // Endpoint: SlowConsumer processes LoadTestMessage with artificial 100 ms latency.
        // Low prefetch and concurrent limits deliberately saturate the consumer so that
        // ADR-004 and ADR-006 back-pressure mechanisms become visible.
        rmq.ReceiveEndpoint("loadtest-processing", e =>
        {
            e.PrefetchCount = 16;
            e.ConcurrentMessageLimit = 8;
            e.Consumer<SlowConsumer, LoadTestMessage>();
        });
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// 6. Load generator (BackgroundService)
// ─────────────────────────────────────────────────────────────────────────────

// Register as Singleton so the API endpoints can resolve the same instance
// to call StartGeneration() / StopGeneration(), and expose as IHostedService
// so the runtime calls ExecuteAsync on startup.
builder.Services.AddSingleton<LoadGenerator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LoadGenerator>());

// ─────────────────────────────────────────────────────────────────────────────
// 7. Observability — OpenTelemetry (traces + metrics) + health checks
// ─────────────────────────────────────────────────────────────────────────────

// AddBareWireObservability replaces NullInstrumentation with BareWireInstrumentation
// and registers BareWireHealthCheck on the standard health check pipeline.
// Health reports Degraded when in-flight messages exceed 90% of MaxInFlightMessages.
builder.Services.AddBareWireObservability(cfg =>
{
    cfg.EnableOpenTelemetry = true;
});

builder.Services.AddHealthChecks();

// ─────────────────────────────────────────────────────────────────────────────
// 8. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 9. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
// Reports Degraded when the bus is under back-pressure (>90% of MaxInFlightMessages).
app.MapServiceDefaults();

// POST /load-test/start — begins publishing LoadTestMessage at the requested rate.
// Optional query parameter: ?rate=N (messages per second, default 1 000, max 100 000).
app.MapPost("/load-test/start", (
    LoadGenerator loadGenerator,
    int? rate) =>
{
    loadGenerator.StartGeneration(rate ?? 1_000);
    return Results.Accepted(value: new
    {
        Status       = "started",
        TargetRate   = rate ?? 1_000,
        StartedAt    = loadGenerator.GenerationStartedAt,
    });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("StartLoadTest");

// POST /load-test/stop — halts the load generator.
app.MapPost("/load-test/stop", (LoadGenerator loadGenerator) =>
{
    loadGenerator.StopGeneration();
    return Results.Ok(new
    {
        Status         = "stopped",
        TotalPublished = loadGenerator.TotalPublished,
        TotalErrors    = loadGenerator.TotalErrors,
    });
})
.Produces(StatusCodes.Status200OK)
.WithName("StopLoadTest");

// GET /metrics — returns current load-test statistics.
// Use this endpoint to observe back-pressure in real time: when IsRunning=true and
// the consumer is saturated, TotalPublished growth slows as PublishAsync awaits.
app.MapGet("/metrics", (LoadGenerator loadGenerator) =>
{
    bool isRunning = loadGenerator.IsRunning;
    long published = loadGenerator.TotalPublished;
    long errors    = loadGenerator.TotalErrors;

    double elapsedSeconds = isRunning
        ? (DateTime.UtcNow - loadGenerator.GenerationStartedAt).TotalSeconds
        : 0;

    double averageRate = elapsedSeconds > 0 ? published / elapsedSeconds : 0;

    return Results.Ok(new
    {
        IsRunning      = isRunning,
        TotalPublished = published,
        TotalErrors    = errors,
        ElapsedSeconds = Math.Round(elapsedSeconds, 1),
        AverageRateMsgPerSec = Math.Round(averageRate, 1),
    });
})
.Produces(StatusCodes.Status200OK)
.WithName("GetMetrics");

app.Run();
