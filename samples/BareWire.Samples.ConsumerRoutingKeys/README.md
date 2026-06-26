# BareWire.Samples.ConsumerRoutingKeys

Demonstrates consume-time routing-key dispatch with three consumers sharing a single queue.

## What this sample shows

| # | Behavior | Routing key | Consumer selected |
|---|----------|-------------|-------------------|
| 1 | **Most-specific-wins** — exact pattern beats wildcard for priority deliveries | `transfer.eu.priority` | `PriorityTransferConsumer` (exact `"transfer.eu.priority"`) |
| 2 | **Multi-consumer routing** — standard EU deliveries match only the wildcard | `transfer.eu.standard` | `RegionTransferConsumer` (wildcard `"transfer.eu.*"`) |
| 3 | **Type-less interop** — foreign JSON without `BW-MessageType` dispatched via `AcceptUntyped()` | `legacy.audit.created` | `LegacyNotificationConsumer` (pattern `"legacy.#"`, `AcceptUntyped()`) |

## Architecture

```mermaid
flowchart LR
    P["UpstreamPublisher\n(RabbitMQ.Client)"]
    X["consumer-routing-keys.transfers\n(topic exchange)"]
    Q["consumer-routing-keys.shared\n(shared queue, binding #)"]
    D["BareWire dispatcher\n(client-side pattern match)"]
    C1["RegionTransferConsumer\ntransfer.eu.*"]
    C2["PriorityTransferConsumer\ntransfer.eu.priority (exact)"]
    C3["LegacyNotificationConsumer\nlegacy.# + AcceptUntyped"]

    P -->|3 deliveries| X
    X -->|binding #| Q
    Q --> D
    D -->|transfer.eu.standard| C1
    D -->|transfer.eu.priority\nexact wins| C2
    D -->|legacy.audit.created\nno BW-MessageType| C3
```

**Key design points:**

- The broker does not segregate traffic — the `#` binding sends every delivery to the shared queue.
- Consumer selection is purely **client-side**: the BareWire dispatcher matches the delivery's `BW-RoutingKey` header against each consumer's declared patterns.
- **Most-specific-wins**: an exact pattern (no wildcards) always beats a pattern containing `*` or `#`. Both `RegionTransferConsumer` (`transfer.eu.*`) and `PriorityTransferConsumer` (`transfer.eu.priority`) match priority deliveries, but the exact pattern wins.
- **AcceptUntyped() is an explicit opt-in** (secure-by-default): without it, typed consumers are never candidates for deliveries that carry no `BW-MessageType` header. The `LegacyNotificationConsumer` opts in explicitly and deserializes the raw payload into `LegacyNotification` (raw-first interop).

## How to run

The easiest way is via the Aspire AppHost, which provisions RabbitMQ automatically:

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

Then trigger a scenario via the HTTP endpoint (replace `<port>` with the port shown in the Aspire Dashboard):

```bash
curl -X POST http://localhost:<port>/run
```

The response lists the three dispatch observations — routing key, consumer name, and whether
the delivery was type-less.

To run standalone (requires a local RabbitMQ broker on `amqp://guest:guest@localhost:5672/`):

```bash
dotnet run --project samples/BareWire.Samples.ConsumerRoutingKeys/
```

## Security caveat

`AcceptUntyped()` exposes the consumer to unauthenticated, producer-controlled foreign JSON.
This sample is self-published and has a zero blast radius, so it omits the production-grade guards.

**Production endpoints using `AcceptUntyped()` must additionally:**
1. Enforce broker-level publish ACLs (e.g. RabbitMQ vhost permissions) to restrict who can publish to the bound exchange.
2. Apply schema validation middleware that checks the payload shape before deserialization.
3. Enforce a payload-size limit to prevent resource exhaustion.

Without these guards, an attacker who can publish to the exchange fully controls the routing key
and payload, and therefore which type-less consumer is selected and what is deserialized.
