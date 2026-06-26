# Consumer Routing Keys

When several consumers share a single queue, you often want each one to handle only a slice of the traffic — EU transfers to one consumer, priority transfers to another, foreign audit events to a third. BareWire lets you express that with **consume-time routing-key dispatch**: each consumer declares a set of AMQP topic patterns, and the BareWire dispatcher selects the right consumer **client-side** by matching the delivery's routing key against those patterns.

This is a **dispatcher predicate, not topology**. Which deliveries land in the queue is still governed by your manually declared bindings (queue→exchange); declaring routing keys on a consumer does not create or alter any binding. A consumer that declares **no** patterns is a catch-all over its message type — exactly the behaviour you had before, with zero change for existing consumers.

## Why consume-time routing

A topic exchange can fan many different routing keys into one queue. Without consume-time routing you would either spin up a separate queue per stream (more topology to manage) or write one big consumer that branches internally on the routing key. Consume-time routing keeps the topology simple — one shared queue — while letting you register several small, focused consumers and have the dispatcher pick the right one per delivery.

The broker does not segregate the traffic; the dispatcher does. That keeps the decision in your application code, where it is testable and visible, and it works the same way across transports.

## Quick start

Use the grouped `Consumer<TConsumer, TMessage>` overload that takes a configuration delegate, and declare the patterns inside it:

```csharp
rmq.ReceiveEndpoint("transfers", e =>
{
    e.Consumer<RegionTransferConsumer, TransferInitiated>(c =>
        c.RoutingKeys("transfer.eu.*", "transfer.pl.*"));
});
```

The configurator exposes three methods (all return `void` — settings are applied imperatively inside the delegate, not fluently chained):

| Method | Effect |
|--------|--------|
| `RoutingKey(string)` | Adds a single topic pattern to this consumer's set. |
| `RoutingKeys(params string[])` | Adds several topic patterns in one call. |
| `AcceptUntyped()` | Opts the consumer in to type-less dispatch (foreign / raw JSON with no message-type header). See [Type-less interop](#type-less-interop-with-acceptuntyped). |

## Topic pattern semantics

Patterns follow standard AMQP topic semantics — the same rules the broker uses for topic bindings, evaluated here client-side at dispatch against the delivery's routing key (carried in the `BW-RoutingKey` transport header):

| Token | Meaning |
|-------|---------|
| `*` | Matches exactly **one** word. |
| `#` | Matches **zero or more** words. |
| `.` | The word separator. |
| (no wildcard) | Matched as **literal equality** (an exact pattern). |

Examples against the delivery routing key `transfer.eu.priority`:

| Pattern | Matches? |
|---------|----------|
| `transfer.eu.priority` | Yes — exact. |
| `transfer.eu.*` | Yes — `*` matches the single word `priority`. |
| `transfer.#` | Yes — `#` matches `eu.priority`. |
| `transfer.*` | No — `*` is one word, but `eu.priority` is two. |
| `transfer.pl.*` | No — `pl` ≠ `eu`. |

## Accumulating patterns

Each `RoutingKey` / `RoutingKeys` call **adds** to the consumer's set — calls accumulate, they do not overwrite. A consumer may legitimately listen on many keys, just like a queue with multiple bindings. Duplicate patterns are idempotent.

```csharp
e.Consumer<RegionTransferConsumer, TransferInitiated>(c =>
{
    c.RoutingKey("transfer.eu.*");                       // set: { transfer.eu.* }
    c.RoutingKey("transfer.pl.*");                       // set: { transfer.eu.*, transfer.pl.* }
    c.RoutingKeys("transfer.de.*", "transfer.fr.*");     // adds two more — earlier patterns kept
});
```

> This is a deliberate difference from the **publish** side. Per-type publish routing is last-call-wins, because an outgoing message carries a single concrete routing key. A consumer, by contrast, is a *set of match patterns*, so repeated calls accumulate.

## Most-specific-wins and tie-break

When a delivery matches the patterns of **more than one** consumer of the same message type, the **most specific** pattern wins. An exact pattern (no wildcards) always beats a pattern containing `*` or `#`; among wildcard patterns, the more specific one wins.

```csharp
rmq.ReceiveEndpoint("transfers", e =>
{
    e.Consumer<RegionTransferConsumer, TransferInitiated>(c =>
        c.RoutingKey("transfer.eu.*"));        // wildcard

    e.Consumer<PriorityTransferConsumer, TransferInitiated>(c =>
        c.RoutingKey("transfer.eu.priority")); // exact — wins for the priority key
});
```

A delivery with routing key `transfer.eu.priority` matches **both** patterns, but the exact `transfer.eu.priority` is more specific than the wildcard `transfer.eu.*`, so `PriorityTransferConsumer` handles it. A `transfer.eu.standard` delivery matches only the wildcard, so it reaches `RegionTransferConsumer`.

If two patterns are equally specific and genuinely ambiguous, the selection is still **deterministic**: the **first-registered** consumer wins and the bus emits a `warning` so you can disambiguate your patterns. The dispatch never depends on hash ordering or chance.

## Default catch-all

A consumer that declares **no** routing-key patterns handles **every** delivery of its message type. This is the default, and it is unchanged from a consumer registered with the parameterless overload:

```csharp
e.Consumer<AuditConsumer, TransferInitiated>();   // no patterns — every TransferInitiated delivery
```

Registering routing keys on *other* consumers does not change the catch-all's reach. For a delivery whose type resolves to `TransferInitiated`, the pattern-matching consumers are considered first; a type catch-all still receives deliveries that carry a routing key matching none of the declared patterns (and the bus logs a `warning` in that case, so an unintended gap is visible rather than silent). Because no-pattern dispatch is the default, adding this feature is fully backward-compatible — existing consumers behave exactly as before.

## Type-less interop with AcceptUntyped()

Foreign producers — anything that is not a BareWire publisher — often send plain JSON with **no message-type header** (`BW-MessageType`). By default BareWire cannot resolve such a delivery to a CLR type, so a typed consumer is never a candidate for it. `AcceptUntyped()` is the explicit opt-in that changes this for one consumer: a delivery with no resolvable type becomes eligible to be dispatched to that consumer purely by routing-key pattern match, and the raw payload is deserialized into `TMessage` (raw-first).

```csharp
e.Consumer<LegacyNotificationConsumer, LegacyNotification>(c =>
{
    c.RoutingKey("legacy.#");
    c.AcceptUntyped();   // opt in to deliveries that carry no BareWire message-type header
});
```

`AcceptUntyped()` is an on/off flag, so calling it more than once has the same effect as calling it once. Without it, a consumer's declared patterns narrow the **typed** dispatch path only — the consumer is never silently exposed to untyped foreign payloads.

## Security: the routing key is untrusted input

The delivery's routing key is **unauthenticated, producer-controlled** input, and client-side pattern matching is a dispatcher predicate — **not** an authorization mechanism. An attacker who can publish to the bound exchange fully controls the routing key and payload, and therefore which `AcceptUntyped()` consumer is selected and what gets deserialized into `TMessage`.

> **Security.** Exposing a consumer to type-less deliveries via `AcceptUntyped()` assumes that publish permissions are enforced at the broker (for example, RabbitMQ publish ACLs / vhost permissions) and that a schema-validation middleware validates the foreign-input axis — routing key, broker identity, and payload shape/size — before deserialization. BareWire emits a **startup warning** when an `AcceptUntyped()` endpoint is configured without such a middleware. Also treat the routing-key value as sensitive: do **not** place it in exception messages or logs.

In short, `AcceptUntyped()` is secure-by-default precisely because it is a conscious, mandatory opt-in: typed consumers never become an accidental sink for untrusted JSON.

## Scenario: many consumers on one queue

One topic exchange, one shared queue bound with `#` (everything), and several consumers that split the traffic by routing key. The broker delivers everything to the queue; the dispatcher routes each delivery to the right consumer.

```csharp
rmq.ConfigureTopology(t =>
{
    t.DeclareExchange("transfers", ExchangeType.Topic, durable: true, autoDelete: false);
    t.DeclareQueue("transfers.shared", durable: true, autoDelete: false, configure: _ => { });

    // One catch-all binding — every routing key reaches the shared queue.
    t.BindExchangeToQueue("transfers", "transfers.shared", routingKey: "#");
});

rmq.ReceiveEndpoint("transfers.shared", e =>
{
    // Standard EU transfers — wildcard.
    e.Consumer<RegionTransferConsumer, TransferInitiated>(c =>
        c.RoutingKey("transfer.eu.*"));

    // Priority transfers — exact pattern beats the wildcard above.
    e.Consumer<PriorityTransferConsumer, TransferInitiated>(c =>
        c.RoutingKey("transfer.eu.priority"));
});
```

A `transfer.eu.standard` delivery reaches `RegionTransferConsumer`; a `transfer.eu.priority` delivery reaches `PriorityTransferConsumer` (most-specific-wins). The topology stayed simple — one exchange, one queue, one binding — and the routing decision lives in application code.

## Scenario: interop with a foreign system

A non-BareWire system publishes plain JSON to the same exchange with a `legacy.*` routing key and **no** message-type header. A type-less consumer picks it up and deserializes it raw-first:

```csharp
rmq.ReceiveEndpoint("transfers.shared", e =>
{
    e.Consumer<LegacyNotificationConsumer, LegacyNotification>(c =>
    {
        c.RoutingKey("legacy.#");
        c.AcceptUntyped();   // explicit opt-in for foreign / type-less deliveries
    });
});
```

A delivery with routing key `legacy.audit.created` and no message-type header matches `legacy.#`, and because the consumer opted in with `AcceptUntyped()`, its raw JSON body is deserialized into `LegacyNotification`. Remember the [security caveats](#security-the-routing-key-is-untrusted-input): guard such endpoints with broker ACLs and schema validation.

## Pitfalls

- **A delivery that matches nothing is unhandled.** For a typed delivery, the pattern-matching consumers are tried first, then any type catch-all consumer (one declaring no patterns) — and a delivery that carried a routing key but matched no declared pattern is logged with a `warning` when it falls through to the catch-all. A delivery is only **truly dropped** when there is no consumer at all for its resolved type — no matching pattern *and* no catch-all. Likewise, a type-less delivery is dropped unless some `AcceptUntyped()` consumer's pattern matches it. Design your pattern sets to cover the traffic you expect, and add a catch-all (or a `#` pattern) if you want a guaranteed sink.
- **Validate foreign input.** Any consumer on an externally-reachable exchange deserializes a producer-controlled payload, so payload validation is good hygiene everywhere; `AcceptUntyped()` raises the stakes because it also removes the message-type gate, letting a consumer accept producer-controlled JSON selected by a producer-controlled routing key. Enforce broker publish ACLs, validate payload shape and size before/within deserialization, and never trust the routing-key value as an authorization signal.

## Running the sample

A working end-to-end demo — one shared queue, three consumers (wildcard, exact, and a type-less `AcceptUntyped()` consumer for foreign JSON) — runs against a real RabbitMQ broker via the Aspire AppHost:

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

Then trigger a scenario over HTTP (the port is shown in the Aspire dashboard):

```bash
curl -X POST http://localhost:<port>/run
```

The response lists, per delivery, the routing key, the consumer that handled it, and whether the delivery was type-less.

> See: `samples/BareWire.Samples.ConsumerRoutingKeys/`

## See also

- [Topology](topology.md) — exchanges, queues, bindings, and routing keys.
- [Publishing and Consuming](publishing-and-consuming.md) — the publish/consume basics and raw-message interop.
- [Per-Key Consumer Ordering](per-key-ordering.md) — ordered consumption per key across competing consumers.
