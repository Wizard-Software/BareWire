# BareWire.Samples.ConsumerDefinitionShowcase

Demonstrates `ConsumerDefinition<TConsumer>` — a per-consumer settings block discovered purely
through explicit DI registration — colocating a retry policy and routing-key patterns next to the
consumer, plus the opt-in transport-level `DeclareTopology` helper applied at endpoint registration.

## What this sample shows

| # | Behavior | Where it lives |
|---|----------|-----------------|
| 1 | **Retry policy inside the definition** — `consumer.Retry(r => r.Exponential(4, 200 ms, 2 s))` | `TransferConsumerDefinition.Configure` |
| 2 | **Routing-key patterns inside the definition** — `consumer.RoutingKeys("transfer.eu.*", "transfer.eu.priority")` | `TransferConsumerDefinition.Configure` |
| 3 | **Opt-in transport topology** — `c.DeclareTopology(exchange, queue, bindingKey, ExchangeType.Topic, durable: false)` | `Program.cs`, at endpoint registration |

## Architecture

```mermaid
flowchart LR
    P["TransferPublisher\n(RabbitMQ.Client)"]
    X["consumer-definition-showcase.exchange\n(topic exchange, DeclareTopology)"]
    Q["consumer-definition-showcase.transfers\n(queue, DeclareTopology)"]
    C["TransferConsumer\n(fail-then-succeed)"]
    D["TransferConsumerDefinition\n(routing keys + retry, DI-discovered)"]

    P -->|"transfer.eu.priority"| X
    X -->|"binding: transfer.#"| Q
    Q --> C
    D -.->|merges settings at bus start-up| C
```

**Key design points:**

- **DI-discovered definition, not inline configuration.** `TransferConsumerDefinition` is registered
  in `Program.cs` as `builder.Services.AddSingleton<ConsumerDefinition<TransferConsumer>, TransferConsumerDefinition>()`.
  There is no assembly scanning: a `ConsumerDefinition<T>` that exists in the assembly but is not
  registered in the container is never applied. The core resolves the registered definition once at
  bus start-up and merges its routing keys and retry policy into the `TransferConsumer` registration
  created by `e.Consumer<TransferConsumer, TransferInitiated>(...)` on the receive endpoint.
- **Retry proof, not simulation.** `TransferConsumer` deliberately throws a transient exception on
  its first two delivery attempts and only records its observation — carrying the attempt number —
  once it succeeds on the third. A recorded `attempts` value greater than 1 is direct proof that the
  definition's `Retry(r => r.Exponential(4, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2)))`
  policy re-delivered the message; nothing in `TransferConsumer` itself performs a retry.
- **Opt-in topology is a separate, transport-level seam.** `DeclareTopology` lives in the RabbitMQ
  transport assembly (not on the transport-agnostic `ConsumerDefinition<T>`) and declares an
  exchange, a queue, and one exchange→queue binding for a single consumer, in one call. Without this
  call no broker entity is created — manual topology (the library default) is unchanged for any
  consumer that never opts in. The AMQP binding key (`"transfer.#"`, broker-side) is a separate axis
  from the dispatcher routing-key patterns (`"transfer.eu.*"` / `"transfer.eu.priority"`, client-side)
  declared on the definition — the two are never coupled.
- **Raw-first, typed dispatch.** `TransferPublisher` is a plain `RabbitMQ.Client` producer (simulating
  a non-BareWire upstream system) that publishes raw JSON with a `BW-MessageType` header so the
  BareWire dispatcher resolves the typed consumer. It declares the same exchange with identical
  parameters to `DeclareTopology` (`type=topic`, `durable=false`, `autoDelete=false`) — any mismatch
  between the two declarations causes the broker to reject the second one with `PRECONDITION_FAILED`.

## How to run

The easiest way is via the Aspire AppHost, which provisions RabbitMQ automatically:

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

Then trigger the scenario via the HTTP endpoint (replace `<port>` with the port shown in the Aspire
Dashboard):

```bash
curl -X POST http://localhost:<port>/run
```

The response carries the run id and a single observation — routing key, consumer name, attempt
count, and transfer id — proving that routing, retry, and the opt-in topology all worked together.

To run standalone (requires a local RabbitMQ broker on `amqp://guest:guest@localhost:5672/`):

```bash
dotnet run --project samples/BareWire.Samples.ConsumerDefinitionShowcase/
```

## Registration lifetime

`ConsumerDefinition<TConsumer>` must be registered as **singleton or transient — never scoped**. The
container discovers and applies registered definitions once at start-up, resolving them from the
root `IServiceProvider`; a scoped registration cannot be resolved outside of a request scope and
would fail at start-up.
