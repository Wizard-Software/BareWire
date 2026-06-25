# Per-Key Consumer Ordering

Competing consumers (multiple instances reading from the same queue) give you horizontal scalability, but they break ordering: messages for the same entity can land on different instances and be processed concurrently — that is, **out of order**. BareWire lets you recover ordering **within a key** while keeping parallelism **across keys** — the "parallel ACROSS keys, ordered WITHIN a key" pattern.

The feature is **off by default**. Without `OrderedBy`/`OrderedByHeader` on the endpoint, the consume path is bit-for-bit identical to plain competing-consumers — zero regression for existing deployments.

## Why per-key ordering

Consider order events: `OrderPlaced`, `OrderShipped`, `OrderDelivered` for the same order MUST be processed in order. With multiple consumer instances and no key affinity, two instances can process two events of the same order at the same time — and persist state in the wrong order.

The solution is to **pin every message of a given key to the same sequential processing path**, while different keys still flow in parallel. The "key" is usually an entity, aggregate, or saga identifier (e.g. `OrderId`, `CustomerId`, `AccountId`).

## Quick start (one-liner)

The simplest form is one of two one-liners on `IReceiveEndpointConfigurator`.

Header variant (raw / cross-language) — the key is read from a transport header:

```csharp
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedByHeader("ordering-key");
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

Typed variant — the key is read from a property of the deserialized message:

```csharp
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedBy<OrderShipped>(m => m.AccountId);
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

Both one-liners set only the **key source** and leave the strategy at `Auto`. The `Auto` strategy is capability-driven: on RabbitMQ it requires a declared transport affinity (see [Transport affinity](#transport-affinity-rabbitmq)) for cross-instance ordering, otherwise it fails fast at startup. For ordering within a single instance, choose the `LocalPartitioned` strategy in the configurator block (below).

## The configurator block

The `OrderedBy(Action<IConsumerOrderingConfigurator>)` overload gives you full control: key source, concurrency, strategy, transport affinity, and poison policy.

```csharp
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.ConcurrentMessageLimit = 16;
    e.OrderedBy(o =>
    {
        o.ByHeader("ordering-key");                              // key source: transport header
        o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
        o.MaxDeliveryAttempts(2);                               // poison / anti-starvation
    });
    e.Consumer<OrderShippedConsumer, OrderShipped>();
    e.Consumer<InventoryAdjustedConsumer, InventoryAdjusted>();
});
```

Methods on the `IConsumerOrderingConfigurator` block:

| Method | Effect |
|--------|--------|
| `ByHeader(string)` | Key source: transport header (raw / cross-language). |
| `By<TMessage>(Func<TMessage, object?>)` | Key source: a selector over the deserialized message. |
| `ByCorrelationId()` | Key source: the auto-stamped correlation-id (fallback in the chain). |
| `Concurrency(int)` | Parallelism across keys in the local layer (the lane count). |
| `Strategy(ConsumerOrderingStrategy)` | Strategy selection. Defaults to `Auto`. |
| `TransportAffinity(TransportAffinity)` | Declares the transport affinity (read at startup; no broker round-trip). |
| `MaxDeliveryAttempts(int)` | Attempt threshold before the poison head is parked and the key released. Defaults to `0` (disabled). |

## Strategies

`ConsumerOrderingStrategy` selects how ordering is enforced:

| Strategy | What it does | Guarantee |
|----------|--------------|-----------|
| `Auto` (default) | Reads the transport's capabilities and the declared `TransportAffinity`; picks the transport-native path when one is declared, otherwise **throws at startup** | Intra + inter-instance (when a path is declared) |
| `LocalPartitioned` | **Only** keyed in-process dispatch (fixed-lane hashing) | **Intra-instance only** — explicitly "single-instance only"; does not preserve ordering across competing instances |
| `TransportNative` | Enforces key→consumer affinity at the transport level (RabbitMQ SAC or consistent-hash) | Intra + inter-instance |

`LocalPartitioned` MUST be selected explicitly — `Auto` will never pick it on its own, because it gives no cross-instance guarantee. Choosing `LocalPartitioned` is a deliberate acceptance of no cross-process affinity.

## Transport affinity (RabbitMQ)

For ordering **across instances** you must pin every key to a single instance at the broker level. RabbitMQ offers two paths, both **opt-in** in an explicitly declared topology (BareWire uses manual topology by default):

### Single-active-consumer (recommended)

`x-single-active-consumer` promotes exactly one active consumer per queue — ordered processing, zero parallelism within the queue. This is the **recommended, default best-practice path**. Declare the queue argument via `IQueueConfigurator.SingleActiveConsumer()` and declare the intent on the endpoint via `TransportAffinity.SingleActiveConsumer`:

```csharp
rmq.ConfigureTopology(t =>
{
    t.DeclareQueue("ordered-processing", durable: true, autoDelete: false, configure: q => q
        .SingleActiveConsumer()
        .DeadLetterExchange("ordered-events-dlx")
        .DeadLetterRoutingKey("ordered-events-dlq"));
});

rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedBy(o =>
    {
        o.ByHeader("ordering-key");
        o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
    });
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

### Consistent-hash exchange (opt-in)

`ExchangeType.ConsistentHash` spreads keys across several bound queues (the same key → the same queue), giving parallelism across keys while preserving ordering within a key. It requires the broker's `rabbitmq_consistent_hash_exchange` plugin to be enabled. Choose this path when you need maximum across-key parallelism and accept an **ordering-loss window on re-map**: adding/removing a bound queue or a node restart re-hashes keys and momentarily breaks per-key ordering. The window is documented and detectable (the transport layer stamps the stream with a mapping epoch, so the consumer can detect and log a re-map), but it is real — which is why SAC remains the default path.

## The two-tier model

BareWire composes per-key ordering from two layers that work together:

- **Local layer (intra-instance).** Keyed in-process dispatch: messages of the same key run sequentially down a single lane (FIFO by arrival order), different keys run in parallel down different lanes. The lane count is controlled by `Concurrency(n)` (falling back to `ConcurrentMessageLimit`). Keys are mapped to a **fixed number of N lanes** (fixed-lane hashing) — different keys may share a lane, like partitions in a broker's partition model. This bounds memory permanently (N lanes, not one lane per key). Lane buffers are bounded **by message count** (lane depth × N lanes) — there are no unbounded buffers.
- **Transport layer (inter-instance).** Key→instance affinity so the same key reaches the same instance (RabbitMQ SAC or consistent-hash, see above).

The global inflight credit (`MaxInFlightMessages`) and the per-key lane depth are **two distinct constraint dimensions** — the global credit gates the pull from the broker, the lane depth protects against a single hot key dominating the budget.

## Fail-fast

When ordering is enabled but neither the transport nor the declared topology guarantees ordering (e.g. RabbitMQ without SAC and without consistent-hash), BareWire **throws `BareWireConfigurationException` at startup** — it never degrades silently and lets unordered messages through. The principle: off by default; when on — a full guarantee or fail-fast. The decision is made deterministically at startup, from configuration alone (no broker query).

An in-process partitioner without a cross-process guarantee is available **only** as an explicit `Strategy(ConsumerOrderingStrategy.LocalPartitioned)`.

## Poison handling / key release

Per-key ordering carries a head-of-line risk: a poison message at the head of a key could block that key's entire stream. The anti-starvation contract: **bounded retry → park/DLQ → key release**.

- The head message is retried up to `MaxDeliveryAttempts` (reusing the endpoint's `RetryCount`/`RetryInterval`).
- Once the threshold is exceeded, the message is dead-lettered (see [Retry and Dead Letter Queues](retry-and-dlq.md)) and **leaves the head** of the key.
- The key's stream **resumes** — subsequent messages are delivered. Skipping the parked message (an ordering gap) is **logged**; there is no "blocked forever" path.

The key is released **only after the broker durably confirms** the head has been parked — if settlement fails, the key is not released (the head stays at the front, ordering unbroken) and the failure is retried.

> **Security:** consumer code should NOT place the ordering-key value in exception messages or logs. Keep a constant message:
>
> ```csharp
> if (context.Headers.TryGetValue("poison-head-demo", out string? flag) && flag == "true")
> {
>     const string poisonHeadMessage =
>         "Simulated poison-head failure. Ordering-key value is omitted from this message.";
>     throw new InvalidOperationException(poisonHeadMessage);
> }
> ```

## Key source and caveats

The key-source chain: an explicit selector/header (`OrderedBy`/`OrderedByHeader`/`By`/`ByHeader`) → fallback to correlation-id (`ByCorrelationId()` or by default) → no key (a keyless message, processed in parallel with no ordering guarantee).

The correlation-id fallback is a **deliberate concession to ergonomics** (a one-liner should "just work"), but it calls for care:

- **Cardinality.** Too low a cardinality (one value for all traffic) creates a hot key that chokes parallelism; an over-volatile key (a different value per message) gives no real affinity — every message is its own "group" and ordering adds nothing.
- **Stability.** A key only makes sense if it is stable per aggregate/entity across its whole lifetime.
- **Availability.** Correlation-id is NOT stamped for a plain `PublishAsync`/`SendAsync` — for that traffic the fallback yields no key, so the message flows without ordering (passthrough).

**The typed selector vs. cross-instance ordering.** The `OrderedBy(m => m.X)` selector reads a CLR property **after deserialization**, which may differ from the key the transport used to route the message to the instance. Therefore:

- the typed selector is **safe for `LocalPartitioned`** (purely local affinity) or when the selector returns exactly the value used for routing;
- for `TransportNative`/`Auto` across instances, **prefer `OrderedByHeader(name)`** with a header name symmetric to the producer side — that is the only path with a "consumer key == routing key" guarantee.

## End-to-end with the outbox

The consumer-side ordering key closes the loop with the producer-side outbox. The outbox in `OrderingMode.PerKey` guarantees ordered hand-off to the broker per key, and a consumer with `OrderedByHeader` preserves that order during processing. A **symmetric header name** ties both sides into one story:

```csharp
// Producer — the outbox stamps and orders by the "ordering-key" header
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(connectionString),
    configureOutbox: outbox =>
    {
        outbox.OrderingMode = OrderingMode.PerKey;
        outbox.OrderingKeyHeaderName = "ordering-key";
    });

// Consumer — reads the same header
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedBy(o =>
    {
        o.ByHeader("ordering-key");
        o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
    });
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

The result: ordered hand-off to the broker **and** ordered processing at the consumer — full end-to-end per-key ordering. See [Transactional Outbox](outbox.md) for the producer side.

## Running the sample

A working end-to-end demo (multiple competing instances via Aspire `WithReplicas(2)`, outbox `OrderingMode.PerKey`, poison-head parking via DLX, both strategy variants) lives in the sample directory:

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

> See: `samples/BareWire.Samples.OrderedConsumers/`
