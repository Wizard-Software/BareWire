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
docker run -d --name postgres -p 5432:5432 -e POSTGRES_PASSWORD=password postgres
```

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

## Shared Projects

- **BareWire.Samples.AppHost** — Aspire orchestrator for all samples (RabbitMQ + PostgreSQL + Dashboard)
- **BareWire.Samples.ServiceDefaults** — Shared OpenTelemetry, health checks, and observability configuration

## Prerequisites

- .NET 10 SDK
- Docker (for Aspire or standalone RabbitMQ/PostgreSQL)
