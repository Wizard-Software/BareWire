# In-Memory Transport

The in-memory transport (`BareWire.Transport.InMemory`) runs publishers and consumers inside one
process and one dependency-injection container. There is no broker, no network hop and no
cross-process delivery. It keeps the same programming model as the broker transports — manual
topology, raw-first payloads, the same consumers and the same topology declarations — so moving
between the in-memory transport and RabbitMQ is a registration change, not a code change.

## When to use it

- **A modular monolith in a single process** — modules exchange events through a real bus with real
  exchanges, queues and bindings, and an external broker would be pure overhead.
- **Local development and tests** — the full pipeline runs without Docker or a broker.

> [!WARNING]
> **Delivery guarantee: at-most-once.** Every queued and in-flight message lives in process memory
> only. A process restart or crash discards all of them — there is no persistence, no write-ahead
> log and no recovery on the next start. Publishers and consumers must live in the same process.
> When you need at-least-once delivery, use a broker-backed transport such as
> [RabbitMQ](transport-rabbitmq.md).

Every publisher and consumer wired through this transport shares the trust boundary of the hosting
process: topology decides routing, it does not restrict which module may publish to or consume from a
queue. See [Differences vs RabbitMQ](#differences-vs-rabbitmq) for what that means for isolation.

### Startup notice

The transport logs its delivery guarantee once per container at startup, so it shows up in the log
stream without anyone reading this page. The notice is a `Warning` only when the host environment
name is exactly `Production` (case-insensitive). Every other name — including variants such as `Prod`
or `prod-eu` — and a container with no `IHostEnvironment` registered log it at `Information`. The
notice is written by a hosted service, so it appears only under a generic host (an `IHost` or an
ASP.NET Core application); starting the bus directly through `IBusControl` does not produce it.

## Registration

As with every BareWire transport, you register the **core engine** and the **transport** together.
Neither the bundle nor `AddBareWire` registers a serializer, so add one explicitly. The examples on
this page use this message and consumer:

```csharp
public sealed record OrderCreated(Guid OrderId);

public sealed class OrderCreatedConsumer : IConsumer<OrderCreated>
{
    public Task ConsumeAsync(ConsumeContext<OrderCreated> context)
    {
        // handle context.Message
        return Task.CompletedTask;
    }
}
```

### 1. Single call — bundle package (recommended)

The `BareWire.InMemory` bundle depends on both the core and the transport and exposes one method,
`AddBareWireWithInMemory`:

```bash
dotnet add package BareWire.InMemory
```

```csharp
using BareWire.Abstractions.Topology;
using BareWire.InMemory;
using BareWire.Serialization.Json;

builder.Services.AddBareWireJsonSerializer();

// Consumers are resolved from DI for every message.
builder.Services.AddTransient<OrderCreatedConsumer>();

builder.Services.AddBareWireWithInMemory(transport =>
{
    transport.ConfigureTopology(topology =>
    {
        topology.DeclareExchange("orders", ExchangeType.Fanout);
        topology.DeclareQueue("order-processing");
        topology.BindExchangeToQueue("orders", "order-processing", routingKey: "#");
    });

    // PublishAsync resolves this exchange when no per-type mapping applies.
    transport.DefaultExchange("orders");

    transport.ReceiveEndpoint("order-processing", e =>
    {
        e.Consumer<OrderCreatedConsumer, OrderCreated>();
    });
});
```

`AddBareWireWithInMemory` takes an optional second `bus` delegate (`Action<IBusConfigurator>`) for
core configuration such as middleware and serializer mappings. Call it at most once per
`IServiceCollection`: it throws a configuration exception, before registering anything, when the
container already holds the core bus or a transport adapter.

### 2. Two calls — core and transport registered separately

```bash
dotnet add package BareWire
dotnet add package BareWire.Transport.InMemory
```

```csharp
using BareWire;
using BareWire.Transport.InMemory;

// ConfigureTopology is the topology method shown in "Switching to RabbitMQ" below.
builder.Services.AddBareWireInMemory(transport =>
{
    transport.ConfigureTopology(ConfigureTopology);
    transport.DefaultExchange("orders");
    transport.ReceiveEndpoint("order-processing", e => e.Consumer<OrderCreatedConsumer, OrderCreated>());
});

builder.Services.AddBareWire();
```

> [!NOTE]
> `DrainTimeout` (see [Graceful shutdown](#graceful-shutdown)) only takes effect through the
> single-call bundle. In the two-call form the value is still validated, but shutdown always uses the
> fixed 10-second default.

### Manual topology by default

Nothing is declared for you. Every queue named by `ReceiveEndpoint` must be declared through
`ConfigureTopology`, otherwise startup fails with a configuration exception. Exchanges, queues and
bindings follow the same model as the broker transports (see [Topology](topology.md)), and the
topology is sealed once the bus has started.

`AutoDeclareEndpointQueues()` is an opt-in shortcut: every receive-endpoint queue that is not already
declared is created at startup — with **no exchanges and no bindings**. Such a queue is reachable only
through the default exchange (an empty exchange name, which routes by queue name), so pair it with
`DefaultExchange("")` and map each message type to its queue:

```csharp
builder.Services.AddBareWireWithInMemory(transport =>
{
    transport.AutoDeclareEndpointQueues();
    transport.DefaultExchange("");                         // route by queue name
    transport.MapRoutingKey<OrderCreated>("order-processing");

    transport.ReceiveEndpoint("order-processing", e => e.Consumer<OrderCreatedConsumer, OrderCreated>());
});
```

Queues are declared only from the endpoints known at startup — never from inbound traffic. For
fan-out, declare the exchange and its bindings explicitly.

### Switching to RabbitMQ

Keep the topology, the default exchange and the receive endpoints in one method that takes the
transport's own configuration methods, and let the registration call be the only thing that differs.
This is the pattern used by the modular-monolith sample in the repository
(`samples/BareWire.Samples.InMemoryModularMonolith`):

```csharp
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Topology;
using BareWire.InMemory;
using BareWire.RabbitMQ;

static void ConfigureTopology(ITopologyConfigurator topology)
{
    topology.DeclareExchange("orders", ExchangeType.Fanout, durable: true, autoDelete: false);
    topology.DeclareQueue("order-processing", durable: true);
    topology.BindExchangeToQueue("orders", "order-processing", routingKey: "#");
}

static void ConfigureMessaging(
    Action<Action<ITopologyConfigurator>> configureTopology,
    Action<string> defaultExchange,
    Action<string, Action<IReceiveEndpointConfigurator>> receiveEndpoint)
{
    configureTopology(ConfigureTopology);
    defaultExchange("orders");
    receiveEndpoint("order-processing", e => e.Consumer<OrderCreatedConsumer, OrderCreated>());
}

if (useInMemory)
{
    builder.Services.AddBareWireWithInMemory(t =>
    {
        t.DrainTimeout(TimeSpan.FromSeconds(10));
        ConfigureMessaging(t.ConfigureTopology, t.DefaultExchange, t.ReceiveEndpoint);
    });
}
else
{
    string rabbitMq = builder.Configuration.GetConnectionString("rabbitmq")
        ?? throw new InvalidOperationException("Missing connection string 'rabbitmq'.");

    builder.Services.AddBareWireWithRabbitMq(r =>
    {
        r.Host(rabbitMq);
        ConfigureMessaging(r.ConfigureTopology, r.DefaultExchange, r.ReceiveEndpoint);
    });
}
```

The consumers, the topology declaration and the module code stay identical. In production, point the
RabbitMQ transport at an `amqps://` URI (or configure TLS) so traffic to the broker is encrypted. Keep
the guarantees in mind when you switch: the broker gives you at-least-once delivery and survives a
restart, the in-memory transport does neither.

## Configuration reference

All options are validated when the transport options are built during registration, so a
misconfiguration fails fast at startup rather than at the first publish. The topology is validated
when the transport adapter is constructed.

| Option | Default | Validation rule |
|---|---|---|
| `QueueCapacity(int)` | `1000` messages per queue | must be greater than zero |
| `SendTimeout(TimeSpan)` | `100 ms` | must be greater than or equal to `TimeSpan.Zero` (`Zero` = never wait) |
| `MaxMessageSize(int)` | `16 MiB` (`16777216` bytes) | must be greater than zero |
| `MaxRedeliveries(int)` | `20` | must be greater than zero; applies to `Requeue` only |
| `DrainTimeout(TimeSpan)` | `10 s` | must be greater than `TimeSpan.Zero`; effective through the bundle only |
| `EnableDefer(TimeSpan?)` | off; `30 s` delay when enabled without a value | delay must be greater than `TimeSpan.Zero` and at most about 49.7 days; cannot be combined with any receive endpoint that declares per-key ordering |
| `GuaranteedRouting()` | off | — |
| `AutoDeclareEndpointQueues()` | off (manual topology) | — |
| `DefaultExchange(string)` | unset | a non-empty name must match a declared exchange |
| Pending scheduled messages | `10,000` | internal limit, no public option (see [Sagas with timeouts](#sagas-with-timeouts)) |

Per-type routing works as on the broker transports: `MapRoutingKey<T>(...)`, `MapExchange<T>(...)` and
`Publish<T>(...)`. Without `GuaranteedRouting()`, a message that matches no binding is dropped and
reported as confirmed (logged at `Warning` and counted); with it, the send result reports the message as
not confirmed instead.

## When a queue is full

Every queue is bounded by `QueueCapacity`. The count includes messages that are queued, being
processed, requeued, or waiting for a deferred redelivery. A send to a full queue never throws and
never blocks indefinitely — the message is reported as **not confirmed** (`SendResult.IsConfirmed =
false`) and counted in `barewire.inmemory.messages.rejected` with `reason=queue_full`.

- **One wait per call.** A send call may wait for room **at most once**, for at most `SendTimeout`
  (100 ms by default), no matter how many messages or target queues in the batch are full. It waits
  only when the full queue has an active consumer. `SendTimeout = TimeSpan.Zero` means never wait.
- **Latch.** A full queue *latches* in two cases: the one wait elapses without room, or the queue is
  full and has no active consumer (it is then rejected at once, without waiting). A latched queue
  rejects every further send immediately and stays latched until its occupancy drops below 50% of
  `QueueCapacity`. A queue that is only momentarily full during a burst is not latched.
- **Per-message outcome.** Each message in a batch is validated and routed on its own; one rejection
  never affects the rest of the batch. An oversized body (`MaxMessageSize`) or an exchange that is not
  declared are rejected the same way, as not confirmed.
- **Fan-out.** `IsConfirmed = true` means a copy reached **every** target queue. If one target
  rejects its copy, the message is reported as not confirmed, but the copies other queues accepted
  stay accepted — a partial fan-out is never rolled back.

**Who sees a rejection.** `IBus.PublishAsync` is fire-and-forget: the caller never receives the send
result and the bus does not retry it, so on that path a rejection is visible only in logs and metrics.
The transactional [outbox](outbox.md) dispatcher does inspect the result and retries a rejected message
until the transport admits it — see [Outbox and Inbox](#outbox-and-inbox).

**Not every rejection is logged.** The `Warning` (when a latch opens) and the matching `Information`
(when it closes, with the number of rejected copies) are written only for latch episodes. When a
consumer is slow but still active, the queue does not latch: surplus copies sent with `PublishAsync`
are dropped with no log entry at all. They show up only as `barewire.inmemory.messages.rejected`
with `reason=queue_full` and the `queue` tag, and as a `Degraded` queue in the health check. Watch
that metric.

## Sizing

### Queue capacity vs. burst

Size `QueueCapacity` for the largest burst the queue must absorb above what its consumer drains,
plus headroom for in-flight and retried work:

```text
QueueCapacity >= (peak publish rate - consumer throughput) x peak burst duration
               + in-flight messages (PrefetchCount)
               + defer rate x DeferDelay
               + requeue headroom
```

For example, a consumer that defers 20 messages per second with the default 30-second defer delay
holds about `20 x 30 = 600` slots on its own — 60% of the default capacity — before any burst.

Treat roughly **100** as the practical floor for `QueueCapacity`. Below that, ordinary publish jitter
is enough to hit the full-queue path and the 90% health alert with no real backlog. It is a lower
bound, not a target — size against the formula above.

### Publisher isolation when a consumer stalls

All publishers in a process share the bus's single publish loop, so what a saturated queue costs
the rest of the process depends on its consumer. There are three regimes:

| Regime | What happens | Cost to the shared publish loop |
|---|---|---|
| Full queue, **no active consumer** | Latches at once, rejects without waiting | None — no wait is ever paid |
| **Stopped or nearly stopped** active consumer, draining `r` msg/s with `r < 1 / SendTimeout` (below 10 msg/s at the default 100 ms) | One wait per saturation episode, then the latch rejects until the queue drains to 50% | Roughly `SendTimeout / (SendTimeout + 0.5 x QueueCapacity / r)` of the loop's time, occasionally up to twice that |
| **Slow but active** consumer, `r >= 1 / SendTimeout` but slower than its publishers | A slot frees within `SendTimeout`, so the wait succeeds and the queue never latches | The loop is paced at about `r` send calls (batches of up to 64 messages) per second, which slows publishes to every other queue as well |

In the second regime, the fractions for `K` independently stalled queues add up, capped at 100% of
the loop's time. At the defaults with a consumer draining 5 msg/s, one queue costs
`0.1 / (0.1 + 0.5 x 1000 / 5)`, about 0.1%; with `QueueCapacity = 100` it is about 1%. That is
another reason to avoid very small capacities: the latch is released after fewer drained messages and
the wait is paid more often. The outbox dispatcher sends outside the bus's publish loop and pays its
own waits.

The formula does **not** apply to the third regime. The transport does not isolate the shared loop
from a consumer that is permanently slower than its publishers: size `QueueCapacity` so bursts fit,
and raise the consumer's throughput (concurrency, more endpoints) when its steady-state rate is below
the publish rate. Keep `SendTimeout` short — it bounds how long one call can stall on a full queue.

### Memory bound

`QueueCapacity x MaxMessageSize` bounds the worst-case message-body memory of one queue — at the
defaults, roughly **16 GiB per queue**. Headers are not counted. The process-wide worst case adds up:

- that per-queue bound for **every declared queue, dead-letter queues included**;
- plus up to **10,000 pending scheduled messages** (saga timeouts) x `MaxMessageSize`, which sit
  outside every queue's capacity for as long as their delay lasts;
- plus a brief second copy of a body while a message is requeued, deferred or dead-lettered.

Bodies are pooled with `ArrayPool<byte>`, which rounds buffers up to the next power of two for sizes
up to 1 GiB; larger bodies are allocated exactly and not reused. A 17 MiB `MaxMessageSize` therefore
costs 32 MiB per slot in the worst case, so prefer a power of two. Returned buffers stay in the shared
pool after the queues drain, so memory can remain high until the runtime trims the pool. Lower
`QueueCapacity` or `MaxMessageSize` when you expect large bodies or many queues.

## Differences vs RabbitMQ

Routing, settlement and dead-lettering mirror the RabbitMQ transport: the same exchange types
(except those listed below), the same five settlement actions, and dead-lettering through a queue's
`DeadLetterExchange` argument. These are the deliberate differences:

| Area | RabbitMQ | In-memory |
|---|---|---|
| Durability | Durable queues and publisher confirms give at-least-once delivery | At-most-once: queued messages are lost on restart or crash |
| Full queue | No length limit by default; with `x-max-length`, the `x-overflow` policy applies (`drop-head` or `reject-publish`) | Always bounded; behaves like `reject-publish` — not confirmed plus a metric, at most one `SendTimeout` wait, then the latch rejects until the queue drains below 50% |
| Invalid message (too large, undeclared exchange) | The broker closes the channel and the adapter raises a transport exception | Only that message is reported as not confirmed, with a reason; the rest of the batch is accepted |
| Full dead-letter queue | The dead-letter queue's own overflow policy applies | The new dead-letter is dropped with a `Warning` and a metric; settlement never waits |
| `Requeue` limit | Classic queues: unlimited; quorum queues: `delivery-limit` | Always limited by `MaxRedeliveries` (20), then dead-lettered or dropped |
| Dead-letter headers | `x-death` header chain | No `x-death`; the original `BW-Exchange` and `BW-RoutingKey` are kept and the redelivery count resets |
| `BW-*` headers | Carried as set by the publisher, subject to header mapping | Stripped and stamped authoritatively (`BW-RoutingKey`, `BW-Exchange`); `BW-MessageType` is the exception and passes through |
| Header mapping and allow-listing | Available | Not available; application headers pass through unfiltered |
| Topology at runtime | Can be declared at any time | Sealed when the bus starts; declaring a different topology later fails. Saga timeouts use the transport's native scheduler instead of runtime delay queues |
| Unsupported features | — | `Headers` and `ConsistentHash` exchanges, queue arguments such as TTL, `x-max-length`, `x-max-length-bytes` and `x-expires`, and `TransportAffinity.ConsistentHash` fail at startup with a configuration error |
| Ordering | Single active consumer via a queue argument | `TransportAffinity.SingleActiveConsumer` is honored natively (one runner per queue in the process) |
| Message size | `max_message_size` (16 MiB by default in 4.x) | `MaxMessageSize` (16 MiB by default); larger bodies are not confirmed with `reason=oversized` |
| Isolation between services | Virtual hosts and per-user permissions | None — every module is the same process |

When modules must be isolated from each other, run them as separate processes on a broker with
permissions, or at least enforce access with an authorization middleware. Sensitive payloads still
need application-level encryption whatever the transport.

**Fan-out with one full queue: parity, not a difference.** This matches RabbitMQ with
`x-overflow=reject-publish`: the message lands in every target queue that accepts it, and the
publisher gets "not confirmed" (the equivalent of a nack) when any target rejected its copy. The
consequence is also the same as on the broker — an outbox retry re-delivers the message to the queues
that already accepted it, and an inbox deduplicates those copies. The only difference is the default:
RabbitMQ has no length limit unless you set `x-max-length`, while in-memory queues are always bounded.

## Outbox and Inbox

The recommended production shape on this transport is the transactional [outbox](outbox.md) in
front of the transport plus the [inbox](inbox.md) behind it. The standard EF Core registration
registers both — the inbox for every endpoint — in one call:

```csharp
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(connectionString),
    configureOutbox: outbox =>
    {
        outbox.PollingInterval = TimeSpan.FromSeconds(1);
        outbox.DispatchBatchSize = 100;
    });
```

Be precise about what this buys you. The outbox dispatcher retries a message until the in-memory
transport **admits** it into a queue. That covers a full or latched queue, but not delivery: a message
the queue has already accepted is still lost if the process restarts or crashes. The outbox is a retry
mechanism layered on top of a transport, not a substitute for a broker.

> [!IMPORTANT]
> **The inbox is required for fan-out consumers when you publish through the outbox.** When one target
> queue of a `Fanout` or `Topic` exchange rejects its copy and the others accept theirs, the outbox row
> is not confirmed and is retried — and every retry delivers the message again to the queues that
> already have it, until the rejecting queue accepts a copy. The inbox deduplicates those copies by
> message id. Use `AddBareWireOutbox`, which always registers the inbox, rather than a custom outbox
> registration without one.

A consumer on this transport can see the same message more than once while the process is running:

- **`Requeue` and `Defer`** deliver the same message again, with an incremented redelivery count.
- **Crash after admission** — the process dies after the queue accepted a message but before the
  outbox row was marked as sent; after the restart the row is sent again, although the consumer may
  already have processed it and saved its effects.
- **Expired row lock** — a long dispatcher cycle lets another dispatcher claim the same row (the
  standard at-least-once behavior of the outbox).
- **Partial fan-out retry** — the case described in the callout above.

Without the outbox (`PublishAsync` directly) there is no retry: a rejected copy is simply lost for that
queue.

The transport also logs a startup `Warning` when an outbox store is registered **without** an inbox
store and at least one consumer queue is fed by a `Fanout` or `Topic` exchange (under a generic host
only). The standard `AddBareWireOutbox` path never triggers it, so do not treat the warning as a safety
net — it only catches custom outbox registrations.

> [!NOTE]
> A publish made **from inside a consumer** currently goes straight to the transport and is not
> buffered by the transactional outbox. Only publishes made outside consume handlers go through the
> outbox. Design fan-out chains with that in mind.

Watch `barewire.inbox.duplicates` to see how many redeliveries the inbox absorbed — see
[Duplicate Metric](inbox.md#duplicate-metric).

## Graceful shutdown

Stopping the bus drains the in-memory queues before consumers are cancelled:

1. **Drain.** Consumers keep running and publishing still works, so a consumer can emit follow-up
   events. The drain ends when every queue **with an active consumer** is empty and the publish loop is
   idle, or when `DrainTimeout` elapses (10 seconds by default), or when the host cancels the stop —
   whichever comes first. Queues without an active consumer, such as dead-letter queues, do not hold the
   drain open.
2. **Cancel consumers.**
3. **Drop the rest.** Messages still sitting in a queue are dropped with one `Warning` per queue,
   carrying the number of dropped messages, and counted with `reason=drain_dropped`. Sends made after
   this point are not confirmed with `reason=closed`.

Keep `DrainTimeout` shorter than the host's own shutdown budget, or the host terminates the process
before the drain completes. Set it through the bundle — the two-call registration ignores it:

```csharp
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

builder.Services.AddBareWireWithInMemory(transport =>
{
    transport.DrainTimeout(TimeSpan.FromSeconds(10));   // below HostOptions.ShutdownTimeout
    // topology and endpoints ...
});
```

## Sagas with timeouts

The in-memory transport schedules messages natively, entirely in process, so a saga's scheduled
timeouts work without deploying any topology at runtime. With `SchedulingStrategy.Auto` (see
[Scheduled Timeouts](saga.md#scheduled-timeouts)) BareWire picks the transport's native scheduler
automatically:

```csharp
var paymentTimeoutSchedule = Schedule<PaymentTimeout>(cfg =>
{
    cfg.Delay = TimeSpan.FromSeconds(30);
    cfg.Strategy = SchedulingStrategy.Auto;
});
```

What to expect:

- The destination is resolved when the timeout is scheduled. A destination that resolves to no
  declared queue, or a delay above about 49.7 days, throws at scheduling time — for a saga that fails
  the message that triggered the timeout, which is then retried or dead-lettered like any other
  processing failure.
- Up to **10,000** scheduled messages can be pending at once; further schedule calls are rejected. They
  take no queue slot while pending, but they do take memory (see [Memory bound](#memory-bound)).
- When a timeout fires, it goes through the normal send path. If its queue has no room it is rejected
  and **never retried** — scheduled delivery is at-most-once, like everything else on this transport. A
  pending timeout is also lost on restart.

> [!IMPORTANT]
> **Cancellation is best-effort.** `CancelTimeout<T>()` issued by a later event of the same saga cancels
> a timeout scheduled by an earlier event of that saga within the same process, and each timeout type is
> cancelled independently. Even so, a timeout handler must still check that the saga is in a state that
> expects the timeout and ignore it otherwise, because cancellation stays best-effort in these cases:
> the process restarted between scheduling and cancelling; the saga runs across multiple instances and
> the cancel call lands on a different instance than the one that scheduled the timeout; the token was
> evicted from the schedule provider's map at its size limit (logged as a warning); the timeout had
> already fired by the time the cancel call ran, a race against the timer; or the same timeout type was
> scheduled again before the previous one was cancelled, so the newer token replaced the older one.

## Metrics, logging and health

Every instrument is reported on the `BareWire` meter, the same one the observability package uses.
It exists only when an `IMeterFactory` is registered (for example through OpenTelemetry's metrics
setup); without one, the transport creates no instruments and every metric call is a no-op. Export it
with `AddMeter("BareWire")` — see [Observability](observability.md).

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `barewire.inmemory.queue.occupancy` | Observable gauge | `{message}` | `queue` |
| `barewire.inmemory.queue.capacity` | Observable gauge | `{message}` | `queue` |
| `barewire.inmemory.queue.latched` | Observable gauge (0 or 1) | `{latch}` | `queue` |
| `barewire.inmemory.queue.latch_episodes` | Counter | `{episode}` | `queue` |
| `barewire.inmemory.messages.rejected` | Counter | `{message}` | `reason`, plus at most one of `queue` or `exchange` |

The `reason` tag is one of `queue_full`, `cancelled`, `closed`, `oversized`, `undeclared_exchange`,
`no_exchange`, `routing_key_too_long`, `internal_error`, `unroutable`, `no_dlx`, `dlx_full`,
`max_redeliveries` and `drain_dropped`. Tag values always come from the declared topology — never from
a publisher-supplied routing key or message id — so cardinality stays bounded. In a fan-out, each
rejected copy is counted against its own queue.

Logging is aggregated, never one entry per message: a latch logs one throttled `Warning` when it opens
and one `Information` when it closes; send-path validation failures, unroutable messages and
settlement drops each log at most once per key per 60-second window. Log entries carry metadata only —
exchange, routing key, queue, reason, counts and body length — never the message body or its headers.

**Health.** The transport reports each queue as `Degraded` once its occupancy reaches 90% of
`QueueCapacity`, and `Healthy` otherwise. The bus health check registered by
`AddBareWireObservability` surfaces it on the standard health endpoints (see
[Health Checks](observability.md#health-checks)). The transition is logged as a `Warning` at 90% and as
an `Information` recovery only once occupancy falls back below 80%, so a queue hovering at the
threshold does not flood the log.

## Testing

### `BareWireTestHarness`

`BareWireTestHarness` (package `BareWire.Testing`) runs on the same in-memory transport engine,
in a private container per harness instance, so tests can run in parallel. It observes **outbound**
publishes and sends only — it does not host consumers or sagas:

```csharp
[Fact]
public async Task PlaceOrder_PublishesOrderCreated()
{
    await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync();

    Task<OutboundMessage> published = harness.WaitForPublishAsync<OrderCreated>(TimeSpan.FromSeconds(5));

    await harness.Bus.PublishAsync(new OrderCreated(Guid.NewGuid()));

    OutboundMessage message = await published;
    message.RoutingKey.Should().Be(typeof(OrderCreated).FullName);
}
```

`WaitForPublishAsync<T>` and `WaitForSendAsync<T>` resolve as soon as a matching message reaches the
transport, or throw `TimeoutException`. A natively scheduled message, such as a saga timeout, bypasses
that observation and is never seen by these methods.

### Consumers and sagas: a real bus in the test

To test a consumer or a saga end to end, build a real bus in the test with `AddBareWireWithInMemory`
and a real serializer:

```csharp
[Fact]
public async Task OrderCreated_IsConsumed()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddBareWireJsonSerializer();
    services.AddSingleton<ReceivedOrders>();
    services.AddTransient<OrderCreatedConsumer>();
    services.AddBareWireWithInMemory(transport =>
    {
        transport.AutoDeclareEndpointQueues();
        transport.DefaultExchange("");
        transport.MapRoutingKey<OrderCreated>("order-processing");
        transport.ReceiveEndpoint("order-processing", e => e.Consumer<OrderCreatedConsumer, OrderCreated>());
    });

    await using ServiceProvider provider = services.BuildServiceProvider();
    IBusControl busControl = provider.GetRequiredService<IBusControl>();
    await busControl.StartAsync();

    var orderId = Guid.NewGuid();
    await provider.GetRequiredService<IBus>().PublishAsync(new OrderCreated(orderId));

    // ReceivedOrders is a test probe the consumer writes to; wait on it with a timeout.
    (await provider.GetRequiredService<ReceivedOrders>().WaitForAsync(orderId, TimeSpan.FromSeconds(5)))
        .Should().BeTrue();

    await busControl.StopAsync();
}
```

`ReceivedOrders` and `OrderCreatedConsumer` are types you define in the test project. Each test gets
its own container and therefore its own in-memory broker — nothing is shared between tests.

### Migrating from MassTransit

| MassTransit | BareWire |
|---|---|
| `services.AddMassTransit(x => x.UsingInMemory((context, cfg) => ...))` | `services.AddBareWireWithInMemory(transport => ...)` |
| `cfg.ConfigureEndpoints(context)` | Explicit `ReceiveEndpoint(...)` per queue, with topology declared in `ConfigureTopology`. `AutoDeclareEndpointQueues()` only creates unbound queues reached by queue name — it is **not** a replacement for the automatic publish/subscribe wiring, so declare exchanges and bindings for fan-out yourself |
| `x.AddConsumer<TConsumer>()` | Register the consumer in DI (`AddTransient<TConsumer>()`) and bind it with `e.Consumer<TConsumer, TMessage>()` |
| In-memory test harness (`ITestHarness`) | `BareWireTestHarness` for outbound messages; a real `AddBareWireWithInMemory` bus for consumers and sagas |

The message and consumer contracts (`IConsumer<T>`, `ConsumeContext<T>`, `IBus`) keep MassTransit's
naming — see [MassTransit Interop](masstransit-interop.md) for wire-level interop with MassTransit
services.

## See also

- [Transports](transports.md) — all transports at a glance
- [RabbitMQ Transport](transport-rabbitmq.md) — the broker to switch to for at-least-once delivery
- [Topology](topology.md) — exchanges, queues and bindings
- [Outbox](outbox.md) and [Inbox](inbox.md)
- [Saga](saga.md) — scheduled timeouts
- [Observability](observability.md) — metrics export and health checks
