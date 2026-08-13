# Consumer Definitions

A **consumer definition** colocates a consumer's settings in one discoverable place, right next to the consumer itself, instead of scattering them across your endpoint-configuration code. You derive from `ConsumerDefinition<TConsumer>`, override a single `Configure` method, and compose that consumer's routing keys, type-less acceptance, MassTransit-envelope interop, and retry policy as one grouped block.

The `ConsumerDefinition` name is familiar from MassTransit — that naming parity is deliberate — but the mechanics differ in one way that trips up migrators, and it is worth stating up front: **a definition is discovered through explicit DI registration, never by scanning your assemblies**. A definition you forget to register is silently ignored. The [Differences vs MassTransit](#differences-vs-masstransit) section at the end covers this in full.

A definition **groups settings that already exist** on the per-consumer configurator; it does not add new consume-time behaviour. Everything a definition can do, you could also do inline in a `ReceiveEndpoint` block — the definition just gives those settings a home.

## Defining and registering a definition

Derive from `ConsumerDefinition<TConsumer>` and override `Configure`. The base class ships a default, empty `Configure`, so a definition with no bespoke logic is a valid no-op.

```csharp
public sealed class OrderConsumerDefinition : ConsumerDefinition<OrderConsumer>
{
    protected override void Configure(
        IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<OrderConsumer> consumer)
    {
        consumer.RoutingKeys("order.eu.*", "order.pl.*");
        consumer.Retry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));
    }
}
```

The definition is generic over the **consumer** type only (`ConsumerDefinition<TConsumer>`), not the message type — see [Message-type inference](#message-type-inference) for why. The `consumer` parameter is a message-agnostic façade (`IConsumerConfigurator<TConsumer>`); the `endpoint` parameter lets you adjust receive-endpoint-level settings.

### Register it through DI

A definition takes effect **only** when it is registered in the container. There is no dedicated `AddConsumerDefinition` helper — register it as the `ConsumerDefinition<TConsumer>` service, mapped to your concrete type:

```csharp
services.AddSingleton<ConsumerDefinition<OrderConsumer>, OrderConsumerDefinition>();
```

At start-up the bus resolves every registered `ConsumerDefinition<TConsumer>` from the root service provider, calls its `Configure`, and merges the result into that consumer's registration. This happens **once**, at start-up — there is no reflection in the hot path and no assembly scan.

> **Register as singleton or transient — never scoped.** Definitions are resolved from the *root* provider at start-up, and a scoped service cannot be resolved outside a scope (the container throws). A definition is stateless configuration, so singleton is the natural choice.

## Composing settings

Inside `Configure` you compose the consumer's settings through the `IConsumerConfigurator<TConsumer>` façade. Every method returns `void` — settings are applied **imperatively** inside the delegate, not fluently chained (this matches the house configurator convention used across the fluent API).

```csharp
protected override void Configure(
    IReceiveEndpointConfigurator endpoint,
    IConsumerConfigurator<OrderConsumer> consumer)
{
    consumer.RoutingKeys("order.eu.*", "order.pl.*");   // dispatcher patterns
    consumer.AcceptUntyped();                            // opt in to type-less deliveries
    consumer.UseMassTransitEnvelope();                   // opt in to MassTransit envelope interop
    consumer.Retry(r => r.Interval(3, TimeSpan.FromSeconds(2)));
}
```

| Method | Effect |
|--------|--------|
| `RoutingKey(string)` | Adds one AMQP topic pattern to this consumer's dispatcher routing-key set. |
| `RoutingKeys(params string[])` | Adds several patterns in one call. |
| `AcceptUntyped()` | Opts the consumer in to type-less dispatch (foreign / raw JSON with no message-type header). |
| `UseMassTransitEnvelope()` | Opts the consumer in to the MassTransit envelope format for both receive and reply. |
| `Retry(Action<IRetryConfigurator>)` | Configures this consumer's retry policy (see the next section). |

The methods differ in how repeated calls combine:

- **`RoutingKey` / `RoutingKeys` accumulate.** Each call *adds* to the set; a consumer may listen on many keys, like a queue with multiple bindings. Duplicate patterns are idempotent. Routing keys are a client-side dispatcher predicate, not topology — see [Consumer Routing Keys](consumer-routing-keys.md) for the full pattern semantics.
- **`AcceptUntyped` and `UseMassTransitEnvelope` are idempotent flags.** They are on/off switches, so calling them twice is the same as once.
- **`Retry` is a scalar knob — last call wins.** Not calling it leaves the endpoint-level retry default in place.

## Retry — the fluent façade

`consumer.Retry(...)` is the ergonomic entry point for a per-consumer retry policy. You pass a delegate that builds the policy through the `IRetryConfigurator` contract. Unlike the rest of the configurator, `IRetryConfigurator` **is** chained — each method returns the same configurator — because retry-policy builders are conventionally fluent:

```csharp
consumer.Retry(r => r
    .Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1))
    .Handle<TransientTransportException>()
    .Ignore<ValidationException>());
```

| Method | Effect |
|--------|--------|
| `Interval(int retryCount, TimeSpan interval)` | Fixed number of retries with a constant delay between attempts. |
| `Incremental(int retryCount, TimeSpan initial, TimeSpan increment)` | Linearly increasing delay — `initial`, then `initial + increment`, and so on. |
| `Exponential(int retryCount, TimeSpan minInterval, TimeSpan maxInterval)` | Exponentially growing delay, bounded between `minInterval` and `maxInterval`. |
| `Handle<TException>()` | Allow-list: retry only for this exception type. |
| `Ignore<TException>()` | Deny-list: never retry for this exception type. |

> `Exponential` takes **three** arguments — the retry count and the lower/upper delay bounds. There is no growth-factor parameter; the delay ramps between `minInterval` and `maxInterval` across the attempt budget.

For dead-lettering and the endpoint-level retry defaults that a per-consumer policy overrides, see [Retry and DLQ](retry-and-dlq.md).

## Security

Two of the settings a definition can compose widen a consumer's input surface, and both are **explicit, conscious opt-ins** precisely so that widening never happens by accident:

- **`AcceptUntyped()`** exposes the consumer to deliveries with no BareWire message-type header — foreign, producer-controlled JSON selected purely by routing-key pattern. A delivery's routing key is unauthenticated, producer-controlled input, and client-side pattern matching is a dispatcher predicate, **not** an authorization mechanism. Guard such consumers with broker-level publish ACLs and a schema-validation middleware that checks the foreign-input axis (routing key, broker identity, payload shape and size) before deserialization. The bus emits a start-up warning when an `AcceptUntyped()` consumer is configured without such a middleware.
- **`UseMassTransitEnvelope()`** changes how a consumer's payload is (de)serialized and whether its reply is wrapped. It is orthogonal to routing-key dispatch: routing keys pick **which** consumer handles a delivery, this opt-in picks **how** its payload is read and written.

Neither setting is ever enabled implicitly. Because a definition puts these opt-ins in one visible place next to the consumer, it is easier — not harder — to audit which consumers accept untrusted input. See [Consumer Routing Keys](consumer-routing-keys.md#security-the-routing-key-is-untrusted-input) for the full trust-boundary discussion.

## Opt-in topology

By default BareWire declares **no** broker entities on your behalf — topology is manual, and declaring a definition changes nothing about which exchanges, queues, or bindings exist. This is deliberate: a definition is transport-agnostic and names no AMQP vocabulary at all.

When you *do* want a consumer to bring its own exchange, queue, and binding, opt in with the RabbitMQ transport's `DeclareTopology` helper. Because it declares AMQP entities, it lives on the transport seam (the `BareWire.Transport.RabbitMQ` assembly), not on the transport-agnostic definition — so you call it where you have the transport's two-parameter configurator, inside a `Consumer<TConsumer, TMessage>` receive-endpoint block:

```csharp
rmq.ReceiveEndpoint("orders", e =>
{
    e.Consumer<OrderConsumer, OrderPlaced>(c =>
        c.DeclareTopology(
            exchange: "orders",
            queue: "orders.q",
            bindingKey: "orders.#",
            exchangeType: ExchangeType.Topic,
            durable: true));
});
```

Two things to keep straight:

- **Opt-in, not default.** Without the `DeclareTopology` call, no broker entity is created — the manual-topology behaviour is unchanged. The declared entities flow through the transport's topology-deployment path and are applied only when topology deployment is explicitly triggered.
- **`bindingKey` is a separate axis from dispatcher routing keys.** The `bindingKey` here is the broker-side AMQP binding key — it governs which deliveries the broker routes into the queue. A consumer's dispatcher routing keys (`RoutingKeys(...)`) govern which of several consumers on that queue handles a delivery *client-side*. Setting one does not set the other; the two are never coupled.

See [Topology](topology.md) for exchanges, queues, and bindings in full.

## Message-type inference

A definition is generic over the consumer, `ConsumerDefinition<TConsumer>` — there is no `TMessage` type parameter. This is intentional: binding the message type at the definition's type level would force its `Configure` signature to reference an unconstrained type parameter, which does not compile. So the message type is **inferred at start-up** instead.

When your consumer implements exactly one `IConsumer<T>`, the message type is unambiguous and BareWire infers it for you — nothing extra to write:

```csharp
public sealed class OrderConsumer : IConsumer<OrderPlaced> { /* ... */ }

// TMessage (OrderPlaced) is inferred at start-up.
services.AddSingleton<ConsumerDefinition<OrderConsumer>, OrderConsumerDefinition>();
```

When a consumer implements **several** `IConsumer<T>` interfaces, inference is ambiguous — there is more than one candidate message type. In that case, name the message type explicitly through the two-parameter `Consumer<TConsumer, TMessage>()` registration overload on the receive endpoint, so each message type is wired independently:

```csharp
rmq.ReceiveEndpoint("orders", e =>
{
    e.Consumer<MultiOrderConsumer, OrderPlaced>();
    e.Consumer<MultiOrderConsumer, OrderCancelled>();
});
```

## Differences vs MassTransit

The name `ConsumerDefinition` is borrowed from MassTransit on purpose, so the concept feels familiar. But there is one behavioural difference that is easy to miss and expensive to debug, so it is worth stating plainly:

> **A definition is discovered through explicit DI registration only. There is no assembly scan.**

In MassTransit, consumer definitions are typically picked up automatically when you point the registration at an assembly to scan. **BareWire does not do this.** A `ConsumerDefinition<TConsumer>` takes effect only if you register it yourself:

```csharp
services.AddSingleton<ConsumerDefinition<OrderConsumer>, OrderConsumerDefinition>();
```

This is the **silent non-registration trap**. A definition you write but forget to register is not an error — it compiles, the app starts, and the consumer simply runs with its endpoint-level defaults as if the definition did not exist. There is no warning that "a definition was found but not applied", because from the container's point of view nothing was found. If a consumer's routing keys, retry policy, or envelope opt-in appear to be ignored, the first thing to check is whether its definition is registered.

The rule is simple: **every definition needs its own `AddSingleton<ConsumerDefinition<TConsumer>, ...>()` line.** Consumers themselves are also registered explicitly — BareWire never auto-registers consumers by scanning either. For the broader picture of what carries over from MassTransit and what does not, see [MassTransit Interop](masstransit-interop.md).

## See also

- [Consumer Routing Keys](consumer-routing-keys.md) — the full routing-key pattern semantics and the `AcceptUntyped()` trust boundary.
- [Retry and DLQ](retry-and-dlq.md) — retry policies and dead-lettering.
- [Topology](topology.md) — exchanges, queues, bindings, and routing keys.
- [MassTransit Interop](masstransit-interop.md) — what carries over from MassTransit and what does not.
- [Configuration](configuration.md) — the fluent bus configuration and DI registration basics.
