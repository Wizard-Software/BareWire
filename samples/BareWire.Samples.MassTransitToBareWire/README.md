# BareWire.Samples.MassTransitToBareWire

Demonstrates **per-consumer message-format selection** — the narrowest of BareWire's three
format axes — by running **two consumers on one shared queue**, where one reads the MassTransit
envelope and the other reads raw JSON.

## The three format-selection axes

BareWire lets you choose the wire format at three widening scopes:

| Axis | Where you set it | Scope |
|------|------------------|-------|
| Bus-global | `AddMassTransitEnvelopeDeserializer()` / `AddMassTransitEnvelopeSerializer()` | Every consumer/endpoint, unless overridden |
| Per-endpoint | `UseSerializer<...>()` / `UseDeserializer<...>()` on a receive endpoint | One endpoint |
| **Per-consumer** | `Consumer<C,M>(c => c.UseMassTransitEnvelope())` | **One consumer** |

Precedence is **per-consumer > per-endpoint > bus-global default**. This sample focuses on the
per-consumer axis: a single endpoint hosts a MassTransit-envelope consumer and a raw consumer
side by side.

## Scenario: mixed consumers on one queue

One receive endpoint (queue `bw-inventory-check`) hosts two consumers with two different formats:

- **`InventoryConsumer` — MassTransit envelope, opted in per consumer.**
  Registered with `Consumer<InventoryConsumer, CheckInventory>(c => c.UseMassTransitEnvelope())`.
  MassTransit's `IRequestClient<CheckInventory>` sends a request wrapped in the MassTransit
  envelope (`application/vnd.masstransit+json`). This consumer reads that envelope and its
  `RespondAsync` replies with a conformant MassTransit **response** envelope, correlated by
  `requestId`. Both receive **and** reply use the envelope — driven by the per-consumer flag,
  not a global content-type guess.

- **`ShipmentConsumer` — raw-first, no opt-in.**
  Registered with `Consumer<ShipmentConsumer, ShipmentNotice>()` (no configurator). It uses the
  default raw format: BareWire's own `IBus.PublishAsync<ShipmentNotice>` publishes plain JSON
  (`application/json`) to a fanout exchange bound to the **same** queue, and this consumer reads
  it raw and emits a raw `ShipmentRecorded` event. No envelope is involved on this path.

The two consumers coexist on one endpoint, each with its own wire format.

## Why consumer registration order matters

Dispatch picks the consumer for each delivery like this:

- A **raw** delivery carries a `BW-MessageType` header, so it is routed **by type** (a fast path) —
  `ShipmentNotice` always goes to `ShipmentConsumer`, regardless of registration order.
- A **MassTransit envelope** carries no such header, so BareWire matches it by trying each
  consumer's deserializer in **registration order** and taking the first that succeeds.

Because of the second rule, the envelope consumer (`InventoryConsumer`) **must be registered
first**. Reversing the order would let the raw consumer attempt to parse the envelope and
misroute it. `Program.cs` keeps `InventoryConsumer` first, with an inline note.

## Architecture

```
MassTransit IRequestClient<CheckInventory>
  -> exchange "bw-inventory-check" (fanout) -> queue "bw-inventory-check"
       -> InventoryConsumer  (UseMassTransitEnvelope: reads MT envelope, replies MT envelope)
       -> reply (MT response envelope, requestId) -> MassTransit auto-reply queue

BareWire IBus.PublishAsync<ShipmentNotice>  (raw JSON)
  -> exchange "bw-shipment-notices" (fanout) -> queue "bw-inventory-check"  (SAME queue)
       -> ShipmentConsumer   (raw-first: reads plain JSON)
       -> IBus.PublishAsync<ShipmentRecorded>
            -> exchange "bw-shipment-events" (topic, routingKey "shipment.recorded")
```

Topology is declared manually — exchanges, the queue, and the bindings are all explicit; nothing
is auto-created.

## Running the sample

The sample is a one-shot console app. It needs a RabbitMQ broker.

```bash
# Point at any broker via the connection-string environment variable:
RABBITMQ_CONNECTIONSTRING="amqp://guest:guest@localhost:5672/" \
  dotnet run --project samples/BareWire.Samples.MassTransitToBareWire
```

The default (when the variable is unset) is `amqp://guest:guest@localhost:5672/`. When run under
the Aspire AppHost, the connection string is injected automatically. The app prints only
`host:port/vhost` — it never echoes broker credentials.

On a successful run you will see the MassTransit request/response round-trip complete, then the
raw `ShipmentNotice` round complete — both handled on the same queue by their respective
consumers.
