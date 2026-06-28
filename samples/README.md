# BareWire Samples

Working examples demonstrating BareWire features with RabbitMQ, PostgreSQL, and .NET Aspire.

## Quick Start

The easiest way to run all samples is via the Aspire AppHost, which provisions RabbitMQ and PostgreSQL automatically:

```bash
dotnet run --project BareWire.Samples.AppHost/
```

This starts all sample applications with shared infrastructure and opens the Aspire Dashboard for observability.

## Running a Single Sample

If you prefer to run a sample individually, start RabbitMQ and PostgreSQL first:

```bash
docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:management
docker run -d --name postgres -p 5432:5432 -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=barewiredb postgres -c max_prepared_transactions=100
```

> **Why `-c max_prepared_transactions=100`?** Some transactional-outbox samples wrap each consume in a
> `System.Transactions.TransactionScope` and also persist business state through **the consumer's own**
> `DbContext` — a second database connection (e.g. `TransactionalOutbox`, `InboxDeduplication`). That
> second connection makes the scope escalate to a two-phase (prepared) commit. PostgreSQL ships with
> `max_prepared_transactions=0` (2PC disabled), so without this flag those consumes abort with
> `55000: prepared transactions are disabled` and every message is dead-lettered. The Aspire AppHost sets
> this automatically; a manual container needs it explicitly.
>
> `OrderedConsumers` avoids 2PC entirely by **sharing the outbox's pinned connection** for its consumer
> write (single-phase commit) — the recommended pattern. See the
> [single-commit vs 2PC guidance](../src/BareWire.Outbox.EntityFramework/README.md).

Then run the sample:

```bash
dotnet run --project BareWire.Samples.BasicPublishConsume/
```

## Samples

### BasicPublishConsume

Publish/subscribe with PostgreSQL persistence. The simplest starting point.

```
POST /messages          — publish a message
GET  /messages          — list received messages
```

### RequestResponse

Synchronous request-response pattern with validation history.

```
POST /validate-order    — send a validation request and get a response
GET  /validations       — list validation history
```

### RawMessageInterop

Interoperability with legacy systems using raw JSON and custom header mapping. A background service simulates an external system publishing raw messages.

```
GET /raw-events         — list raw events received
GET /typed-events       — list typed events received
```

### RabbitMQ

Full-featured example combining SAGA state machine, transactional outbox, observability, and flow control.

```
POST /orders            — create an order (triggers saga)
GET  /orders            — list orders
```

### BackpressureDemo

Demonstrates consume-side and publish-side flow control under load.

```
POST /load-test/start?rate=1000   — start publishing at given rate
POST /load-test/stop              — stop the load generator
GET  /metrics                     — real-time throughput and backpressure status
```

### RetryAndDlq

Retry policies with RabbitMQ native Dead Letter Exchange handling. Simulates 70% payment failure rate.

```
POST /payments          — submit a payment (70% chance of failure)
GET  /payments/failed   — list payments that landed in the DLQ
```

### SagaOrderFlow

Complex order lifecycle with compensable activities (stock reservation, payment, shipment) and a 30-second payment timeout.

```
POST /orders            — create an order (triggers full saga flow)
GET  /orders/{id}/status — check current saga state
```

### TransactionalOutbox

Exactly-once delivery via atomic outbox writes and inbox deduplication. Messages survive broker downtime.

```
POST /transfers         — initiate a transfer (written atomically with outbox)
GET  /outbox/pending    — count of undispatched outbox messages
```

### ObservabilityShowcase

3-hop distributed trace (order → payment → shipment) with OpenTelemetry, topic exchange routing, and SAGA integration. View traces in the Aspire Dashboard.

```
POST /demo/run          — trigger the full 3-hop flow
```

### MultiConsumerPartitioning

Multiple consumer types on a single endpoint with per-CorrelationId ordering via 64-partition middleware.

```
POST /events/generate        — publish 1000 events across 10 CorrelationIds
GET  /events/processing-log  — verify per-correlation ordering
```

### OrderedConsumers

End-to-end per-key consumer ordering with competing consumer instances (Aspire `WithReplicas(2)`),
transactional outbox (`OrderingMode.PerKey`), and poison-head parking via DLX.

The consumers persist a `ProcessedRecord` through the **same connection the outbox middleware pinned**
for the in-flight message (via `IOutboxConnectionAccessor`), so the business write commits **single-phase**
with the outbox and inbox writes — one commit, no two-phase (prepared) commit, and no
`max_prepared_transactions` requirement on PostgreSQL.

Demonstrates two ordering tiers (ADR-026):

1. **Cross-instance (SAC)** — `ordered-processing` queue with `x-single-active-consumer`. RabbitMQ
   promotes exactly one active consumer across replicas; ordered delivery is guaranteed per key
   across process boundaries. `MaxDeliveryAttempts(2)` parks a poison head via DLX and releases
   the key stream.
2. **Single-instance (LocalPartitioned)** — `local-partitioned-processing` queue with a typed
   selector `m => m.AccountId` and fixed-lane hashing (`Concurrency(8)`). Cross-instance safe only
   under `LocalPartitioned` (M3 caveat documented in source).

Run via Aspire AppHost (recommended — starts RabbitMQ + PostgreSQL + 2 replicas automatically):

```bash
dotnet run --project BareWire.Samples.AppHost/
```

```
POST /events/generate?withPoison=false  — publish 3 healthy accounts × 5 sequences via outbox
POST /events/generate?withPoison=true   — same + inject a synthetic poison key (seq=0 parked, 1..4 resume)
GET  /events/processing-log             — verify strict per-key ordering across competing replicas
GET  /health                            — health check
```

**Non-PII note:** ordering keys in this sample (`acct-A`, `acct-B`, `acct-C`) are synthetic
demonstration values and do not identify natural persons. The poison key is a generated Guid
fragment and never appears in query strings or response bodies.

### ConsumerRoutingKeys

Consume-time routing-key dispatch with three consumers sharing a single queue. A topic exchange
delivers all traffic to the shared queue; the BareWire dispatcher selects the correct consumer
client-side by matching routing-key patterns — the broker topology does not segregate traffic.

Demonstrates three behaviors:
1. **One shared queue, many consumers** — all deliveries land in one queue; consumer selection is purely client-side.
2. **Most-specific-wins** — `transfer.eu.priority` (exact) beats `transfer.eu.*` (wildcard) for priority deliveries.
3. **Type-less interop** — a raw producer omits the `BW-MessageType` header; the consumer opted in via `AcceptUntyped()` receives the delivery and deserializes it raw-first.

```
POST /run   — publish 3 deliveries, wait for all consumers, return dispatch observations
```

## Shared Projects

- **BareWire.Samples.AppHost** — Aspire orchestrator for all samples (RabbitMQ + PostgreSQL + Dashboard)
- **BareWire.Samples.ServiceDefaults** — Shared OpenTelemetry, health checks, and observability configuration

## Prerequisites

- .NET 10 SDK
- Docker (for Aspire or standalone RabbitMQ/PostgreSQL)
