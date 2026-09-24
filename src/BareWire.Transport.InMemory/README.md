# BareWire.Transport.InMemory

An in-memory, single-process transport for BareWire. Publishers and consumers run inside the same
process and the same dependency-injection container; there is no network hop, no broker, and no
cross-process delivery. Useful for local development, integration tests, and any deployment where a
single process is acceptable and an external broker would be pure overhead.

Configuration follows the same manual-topology model as the broker-backed transports (RabbitMQ,
Kafka, ...): exchanges, queues, and bindings are declared explicitly, and switching between this
transport and a broker transport is a registration change, not a code change, for consumers and for
the topology declaration.

## Installation

```bash
dotnet add package BareWire.Transport.InMemory
```

## Usage

```csharp
services.AddBareWireInMemory(t =>
{
    t.ConfigureTopology(topology =>
    {
        topology.DeclareExchange("orders", ExchangeType.Topic);
        topology.DeclareQueue("order-processing");
        topology.BindExchangeToQueue("orders", "order-processing", "order.*");
    });

    t.ReceiveEndpoint("order-processing", e =>
    {
        e.Consumer<OrderProcessingConsumer, OrderCreated>();
    });
});

services.AddBareWire();
```

Call `AddBareWireInMemory` once per dependency-injection container, alongside `AddBareWire()`. A
second call on the same container still validates its options but is otherwise ignored as a whole — it
does not create a second in-memory broker, replace the per-type routing mappings, or merge the two
configurations.

## Delivery guarantee

The in-memory transport is **explicitly at-most-once**. Every queued and in-flight message lives in
process memory only:

- A process restart or crash discards every queued and in-flight message unconditionally — there is
  no persistence, no write-ahead log, and no recovery on the next start.
- All publishers and consumers configured through this transport share a single process and a single
  dependency-injection container; there is no cross-process or cross-machine delivery.
- A rejection on the direct `PublishAsync` / send path (a full queue, an oversized body, an
  unresolved exchange, or an unroutable message when `GuaranteedRouting` is enabled) is **not**
  returned to the caller — `IBus.PublishAsync` is fire-and-forget and the bus publishing loop does
  not retry it. The rejection is visible only in the transport's logs and metrics on that path.
- The transactional outbox dispatcher does inspect the send result and retries a rejection until the
  transport accepts the message. That retry only covers *admission* into the in-memory queue —
  messages the queue has already accepted are still lost on a process restart or crash. The outbox is
  a retry mechanism layered on top of a transport, **not an alternative to a broker**: when
  at-least-once delivery across a restart is required, use a broker-backed transport instead.
- A fan-out publish retried by the outbox after a partial failure can re-deliver the message to
  queues that already accepted it on the first attempt. Consumers that fan out from an outbox-driven
  publish should use an Inbox to deduplicate redelivered messages.

When at-least-once delivery is required, use a broker-backed transport (RabbitMQ, Kafka, ...)
instead of this one.

## Startup diagnostics

`AddBareWireInMemory` registers a hosted service that logs the delivery guarantee above exactly once
at startup, so it is visible in the log stream without reading this document. The notice is logged at
`Warning` only when the registered `IHostEnvironment.EnvironmentName` is exactly `"Production"`
(case-insensitive); every other environment name — including a variant such as `"Prod"` or
`"prod-eu"` — and the case where no `IHostEnvironment` is registered at all both log at
`Information`. The hosted service runs only under a generic host (an `IHost` or an ASP.NET Core
application) that starts registered hosted services; starting the bus directly through `IBusControl`
without a generic host does not trigger this notice.

A second, separate `Warning` is logged when the transactional outbox is registered without an inbox
and at least one consumer queue is fed by a `Fanout` or `Topic` exchange — an outbox retry can
re-deliver a message to a queue that already accepted it on a prior attempt, and without an inbox
that redelivery is not deduplicated. The standard EF Core outbox registration
(`BareWire.Outbox.EntityFramework`'s `AddBareWireOutbox`) always registers an inbox for every
endpoint alongside the outbox, so this warning does not appear on that path — it is reserved for a
custom outbox registration that registers the outbox store without an inbox store.

## Trust boundary

All publishers and consumers wired through this transport share the trust boundary of the hosting
process. Topology and the default exchange only affect message routing — they do not restrict which
module in the process may publish to or consume from a given queue. When isolation between
publishers and consumers is required, enforce it with an authorization middleware, or use a
broker-backed transport with its own access-control model. Sensitive payload content still requires
application-level encryption regardless of transport. Header name mapping and header allow-listing
are not available on this transport; application headers pass through without filtering.

## Sending

`SendBatchAsync` accepts a batch of outbound messages and returns one `SendResult` per message, in the
same order. Each message is validated and routed independently — a rejection of one message never
affects the others.

`IsConfirmed = true` means a copy of the body reached **every** target queue the message routed to. It
is an admission signal, not a durability guarantee: in-memory delivery is explicitly at-most-once (see
above). A fan-out message accepted by only some of its target queues is reported as `false` overall,
but the copies that were accepted stay accepted — a partial fan-out failure is never rolled back.

The specific rejection reason (`queue_full`, `cancelled`, `closed`, `oversized`, `undeclared_exchange`,
`no_exchange`, `routing_key_too_long`, `internal_error`) is never returned to the caller — it is only
observable through this adapter's logs and metrics, aggregated per (queue or declared exchange, reason)
and throttled to one log entry per key per 60-second window, never logged per message. The `oversized`
and `routing_key_too_long` reasons are only tagged with the exchange name when that exchange is declared
in the topology — an undeclared, publisher-supplied exchange name never becomes a metric tag or a log
key. Validation places no upper bound on `MaxMessageSize` itself; see "Memory bound" above for the
resulting worst-case footprint.

A full target queue with no active consumer latches itself immediately and rejects further sends without
waiting — the "Publisher isolation when a consumer stalls" and "Queue capacity vs. burst" sections above
already describe that latch, its 50%-occupancy release hysteresis, and the one-wait-per-call budget in
full. To restate the one-wait rule precisely: a call waits **at most once**, for at most `SendTimeout`,
no matter how many messages or target queues in the batch are full when it runs — every full queue
reached before that one wait is spent is retried within the same shared budget; every one reached
afterwards is rejected immediately, with no further waiting. `SendTimeout = TimeSpan.Zero` means "never
wait": a full queue is always rejected immediately, and a queue that would otherwise be latched by a
failed wait is only ever latched by `TryReserve` itself (full, no active consumer), never by a send call.

Cancelling the call's token while its one wait is in flight never throws
`OperationCanceledException`: the message that was waiting, its still-pending target queues, and every
later message in the batch are reported as not confirmed, and the call returns normally. A token already
cancelled before any message is processed does throw, however, since nothing has been admitted yet.

Once the adapter is disposed, a send call — and any message of a call already in flight — reports
`false` with reason `closed` instead of throwing `ObjectDisposedException`. A send that is waiting
for queue capacity at that moment completes immediately with reason `closed` rather than waiting
out `SendTimeout`. Messages still sitting in a queue when the adapter is disposed are dropped, with
one `Warning` per queue carrying the number of dropped messages and reason `drain_dropped` on the
shared rejected-messages counter (see "Metrics, logging and health" below), and their buffers are
returned to the pool.

## Settlement

`SettleAsync` mirrors RabbitMQ's settlement map — the same five actions, the same dead-lettering
behavior for a rejected message, and the same opt-in for delayed redelivery:

| Action | Behavior |
|---|---|
| `Ack` | Releases the source queue's slot. The message's own `Dispose()` returns its buffer — settlement never touches it. |
| `Nack` / `Reject` | Dead-letters the message (see below), or drops it with a `Warning` log and a metric when no dead-letter target accepts it. |
| `Requeue` | Puts a copy back at the head of the source queue with an incremented redelivery count, reusing the same reserved slot — unless `MaxRedeliveries` is already reached, in which case it dead-letters instead. |
| `Defer` | Throws `NotSupportedException` unless `EnableDefer()` is configured. With it, schedules a copy for write-back to the same, already-reserved slot after `DeferDelay` — see "Deferred redelivery" below. |

### Dead-lettering

A queue declared with a `DeadLetterExchange` queue argument routes a `Nack`, a `Reject`, or a
`Requeue` past `MaxRedeliveries` to that exchange, exactly like RabbitMQ:

- The routing key is the `DeadLetterRoutingKey` override when one is declared, or the message's
  original routing key otherwise.
- Admission to every dead-letter target queue is **non-blocking** — settlement never waits for
  capacity. A full or latched target queue simply rejects the new dead-letter; it is dropped with a
  `Warning` log and a metric instead of being retried or blocking the settling consumer.
- A fan-out dead-letter exchange copies the body to every target queue that has room; a queue with no
  room gets no copy.
- A dead-lettered message keeps its original `BW-Exchange` and `BW-RoutingKey` headers (the ones
  stamped when it was first published), not the dead-letter exchange's own name — this differs from
  RabbitMQ's `x-death` header chain, which this transport does not implement. Its redelivery count is
  reset to zero, as it is a fresh delivery on its new queue.

A drop during settlement is reported with one of these reasons (a log field and a metric tag, never
the message body or headers): `no_dlx` (no dead-letter exchange declared), `dlx_full` (every target
queue was full or latched), `unroutable` (the declared dead-letter exchange matched no binding for the
resolved routing key), or `max_redeliveries` (a `Requeue` past the limit with no dead-letter exchange
declared).

**Dead-letter loop caveat.** A dead-letter exchange that routes back to its own source queue creates a
loop: `MaxRedeliveries` bounds a `Requeue` loop on a single queue, but it does **not** bound a
`Nack`/`Reject` → dead-letter → `Nack`/`Reject` loop across queues, because a fresh dead-letter always
starts at redelivery count zero. This is the same behavior as RabbitMQ — avoid pointing a queue's
dead-letter exchange back at itself (directly, or through a chain of exchanges) unless every consumer
in that chain eventually acknowledges instead of rejecting.

`Requeue`'s cost is proportional to the source queue's current depth (it drains and rewrites the
underlying channel to insert at the head) — bounded in practice by `MaxRedeliveries`, since every
`Requeue` past that limit dead-letters instead of requeuing again.

### Deferred redelivery

`EnableDefer(delay)` opts a whole in-memory transport into the `Defer` settlement action: a deferred
delivery holds its queue's reserved slot for `delay`, then is written back to the same queue with an
incremented redelivery count — never checked against `MaxRedeliveries`. Without `EnableDefer()`,
`Defer` always throws `NotSupportedException` and leaves the delivery in flight, settleable with
another action.

`EnableDefer()` is **incompatible with per-key ordering**: a receive endpoint that declares
`OrderedBy` (with any `TransportAffinity`, including `SingleActiveConsumer`) rejects a deferred
redelivery from ever reordering its stream, so combining the two throws a configuration exception when
the transport is registered, before any message is ever consumed.

## Metrics, logging and health

Every instrument this transport creates is reported on one meter, named `BareWire` — the same meter
`BareWire.Observability` registers its own bus-level instruments on. The meter is created only when an
`IMeterFactory` is registered in the dependency-injection container (for example via OpenTelemetry's
`AddMetrics()`); without one, this transport creates no instrument at all and every metric call below
is a no-op, matching the rest of BareWire's opt-in, zero-cost-when-unused observability. Add
`AddMeter("BareWire")` to an OpenTelemetry `MeterProviderBuilder` to export it.

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `barewire.inmemory.queue.occupancy` | Observable gauge (`int`) | `{message}` | `queue` |
| `barewire.inmemory.queue.capacity` | Observable gauge (`int`) | `{message}` | `queue` |
| `barewire.inmemory.queue.latched` | Observable gauge (`int`, 0 or 1) | `{latch}` | `queue` |
| `barewire.inmemory.queue.latch_episodes` | Counter (`long`) | `{episode}` | `queue` |
| `barewire.inmemory.messages.rejected` | Counter (`long`) | `{message}` | `reason`, and at most one of `queue` or `exchange` |

**Tags are only ever `queue`, `exchange`, and `reason` — never a publisher-supplied routing key or
message ID.** A `queue` or `exchange` tag value always comes from this transport's own sealed
topology (a declared queue or exchange name), never from caller-controlled, unbounded-cardinality
input; a rejection with no queue or exchange that has any meaning for the batch (the adapter was
closed, or the call was cancelled) carries `reason` alone. Log entries, by contrast, do carry
metadata beyond those three fields — a routing key, or an undeclared, publisher-supplied exchange
name — but never a message body, its headers, or its message ID.

`barewire.inmemory.messages.rejected`'s `reason` tag is one of: `queue_full`, `cancelled`, `closed`,
`oversized`, `undeclared_exchange`, `no_exchange`, `routing_key_too_long`, `internal_error` (the
`SendBatchAsync` rejection reasons — see "Sending" above), `no_dlx`, `dlx_full`, `unroutable`,
`max_redeliveries` (the settlement drop reasons — see "Dead-lettering" above), and `drain_dropped`
(messages dropped when the adapter is disposed — see "Sending" above).

Logging is aggregated, never one entry per message:

- A queue's "full" latch logs one throttled `Warning` when it opens (at most once per queue per
  60-second window, carrying how many earlier episodes in that window were suppressed) and, only for
  an episode whose `Warning` was actually logged, exactly one `Information` when it closes — with the
  number of rejected copies and the episode's duration.
- A validation failure or an unexpected exception on the send path logs one throttled `Error` per
  (declared exchange or queue, reason) pair, at most once per 60-second window.
- An unroutable message logs one throttled `Warning` per exchange, at most once per 60-second window.
- A settlement drop (dead-lettering failure, or a redelivery limit reached with no dead-letter
  exchange) logs one throttled `Warning` per (queue, reason) pair, at most once per 60-second window.

`GetHealth()` (surfaced through the bus health check) reports a queue `Degraded` once its occupancy
reaches at least 90% of its capacity, `Healthy` otherwise, on every call — with no hysteresis of its
own. The LOG of that transition, however, is hysteresed separately from the reported status: a
`Warning` is logged the moment occupancy first reaches 90%, and the matching `Information` recovery is
logged only once occupancy drops back below 80% — the 80–90% band logs nothing in either direction, so
a queue oscillating across the 90% line alone does not flood the log.

## Sizing

### Queue capacity vs. burst

Size `QueueCapacity` for the largest burst you expect the queue to absorb above what its consumer can
drain, plus headroom for in-flight and retried work:

```
QueueCapacity ≥ (peak publish rate − consumer throughput) × peak burst duration
              + in-flight messages (PrefetchCount)
              + defer rate × DeferDelay
              + requeue headroom
```

Example: a consumer that defers 20 messages/second with the default 30-second `DeferDelay` occupies
roughly `20 × 30 = 600` queue slots on its own — 60% of the default 1000-message capacity — before
counting any burst or in-flight messages.

A capacity below roughly 100 is rarely useful in practice: at that size, ordinary publish jitter is
enough to hit the full-queue path and the 90%-occupancy health alert on its own, independent of any
real backlog. This is a **lower bound**, not a comfortable default — size it against the formula
above, not just against this floor.

### Publisher isolation when a consumer stalls

A `SendBatchAsync` call waits **at most once**, for at most `SendTimeout`, no matter how many
messages in the batch target a full queue — one wait per call, not one wait per message.

A consumer that stops entirely costs its publishers one such wait per saturation episode. After that
wait elapses without room freeing up, the queue latches and rejects further sends immediately (no
further waiting) until its occupancy drops back below 50% of `QueueCapacity`.

All publishers in a process share the bus's single publish loop, so what a saturated queue costs
depends on whether its consumer frees a slot within `SendTimeout`.

**Near-stalled consumer** — draining at rate `r` messages/second with `r < 1 / SendTimeout` (below
10 msg/s at the default 100 ms). The wait elapses without room, the queue latches, and it stays
latched until the consumer drains it to 50% of `QueueCapacity`. Each such episode costs the shared
publish loop roughly this fraction of its time (occasionally up to twice as much, when a slot frees up
during the first wait and the latch is only set after a second, failed wait):

```
SendTimeout / (SendTimeout + 0.5 × QueueCapacity / r)
```

For `K` independently near-stalled queues the fractions add up (capped at 100% of the loop's time).
Example at the defaults with a consumer draining 5 msg/s: `0.1 / (0.1 + 0.5 × 1000 / 5)` ≈ 0.1%; with
`QueueCapacity = 100` it is ≈ 1%. This is why a very small capacity is a poor fit: the latch is
released after fewer drained messages and the loop pays the wait more often.

**Slow but active consumer** — `r ≥ 1 / SendTimeout`, yet slower than its publishers. A slot frees
up before `SendTimeout` elapses, so the wait succeeds and the queue never latches. Every call that
reaches the still-full queue waits again, roughly `1 / r` per call, so for as long as publishers
keep outrunning that consumer, **the shared publish loop is paced at about `r` send calls per
second** — publishes to every other queue in the process are slowed as well (the other messages of a
call still go through; a call waits at most once). The formula above does not
apply in this regime. The transport does not isolate the shared loop from a consumer that is
permanently slower than its publishers: size `QueueCapacity` so that bursts fit (see above), and raise
the consumer's throughput (concurrency, more endpoints) when its steady-state rate is below the
publish rate.

Keep `SendTimeout` short — it bounds how long a single call can wait on a full queue.

### Memory bound

`QueueCapacity` × `MaxMessageSize` bounds the worst-case message-body memory footprint of a single
queue — at the defaults, roughly 16 GiB. The process-wide worst case is that per-queue bound
multiplied by the number of declared queues, including dead-letter queues; headers are not counted
toward this bound. Message bodies are pooled via `ArrayPool<byte>`, whose buffers are rounded up to
the next power-of-two size — the effective worst-case footprint is therefore rounded up to that
boundary too (e.g. a 17 MiB `MaxMessageSize` actually reserves 32 MiB per slot). Prefer a
power-of-two `MaxMessageSize` to avoid paying for headroom you did not ask for. Returned buffers stay
in the shared pool after the queues drain, so memory may remain high until the runtime trims the pool
under memory pressure; a requeued or deferred message briefly holds a second copy of its body. Lower `QueueCapacity`
or `MaxMessageSize` when large message bodies or many queues are expected.

## Configuration defaults

| Option | Default | Validation rule (checked at registration) |
|---|---|---|
| `QueueCapacity` | `1000` | must be greater than zero |
| `SendTimeout` | `100 ms` | must be greater than or equal to `TimeSpan.Zero` (`Zero` = never wait) |
| `MaxMessageSize` | `16 MiB` (`16777216` bytes) | must be greater than zero |
| `MaxRedeliveries` | `20` | must be greater than zero |
| `DrainTimeout` | `10 s` | must be greater than `TimeSpan.Zero` |
| `DeferEnabled` / `DeferDelay` | off / `30 s` | when enabled, `DeferDelay` must be greater than `TimeSpan.Zero` and at most about 49.7 days (the timer due-time limit), and no receive endpoint may declare `OrderedBy` (see "Deferred redelivery" above) |
| `GuaranteedRouting` | off | — |
| `AutoDeclareEndpointQueues` | off (manual topology) | — |
| `DefaultExchange` | unset | — |

All validation runs when `AddBareWireInMemory` builds its options, so a misconfiguration fails fast
during application startup rather than at the first publish or consume call.
