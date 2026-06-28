# RabbitMQ Transport

RabbitMQ is BareWire's **reference transport** (`BareWire.Transport.RabbitMQ`) and the broker most
of this site is written against. It maps BareWire's core concepts directly onto AMQP 0-9-1:
exchanges, queues, and bindings declared as manual topology, plus consistent-hash and
single-active-consumer for per-key ordering, native dead-letter exchanges, and publish-style
request/response.

Because RabbitMQ is the default throughout the documentation, this page is a transport-specific
reference — registration, connection and TLS, settlement, and a map to the feature guides — rather
than a re-explanation of every feature. The deep guides are linked at the end of each section.

## Registration

As with every BareWire transport, you register the **core engine** and the **RabbitMQ transport**
together. See [Configuration](configuration.md#bus-registration) for the full rationale behind the
layering.

### 1. Single call — bundle package (recommended)

The `BareWire.RabbitMQ` bundle depends on both the core and the transport and exposes one method,
`AddBareWireWithRabbitMq`. Configure the transport in the first delegate and (optionally) the bus
in the second:

```csharp
builder.Services.AddBareWireWithRabbitMq(
    transport =>
    {
        transport.Host("amqp://guest:guest@localhost:5672/");
        transport.ConfigureTopology(t => { /* exchanges, queues, bindings */ });
        transport.ReceiveEndpoint("orders", e => e.Consumer<OrderConsumer, OrderCreated>());
    },
    bus =>
    {
        bus.AddConsumer<OrderConsumer>();
        // serializers, middleware...
    });
```

The `bus` delegate is optional — omit it when transport defaults are enough:

```csharp
builder.Services.AddBareWireWithRabbitMq(transport => transport.Host("amqp://localhost"));
```

### 2. Two calls — core and transport registered separately

`AddBareWireWithRabbitMq` is sugar over the explicit pair. Use the two-call form when you reference
the core and transport packages separately, or when an application registers more than one
transport (a bundle call registers the core internally, so two bundle calls would register it
twice):

```csharp
builder.Services.AddBareWireRabbitMq(transport => transport.Host("amqp://guest:guest@localhost:5672/"));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

> **Deprecated:** configuring the transport with `cfg.UseRabbitMQ(...)` *inside* the `AddBareWire`
> delegate is an obsolete no-op — any host/credentials passed to it are ignored. Configure the
> transport through `AddBareWireRabbitMq` or the bundle. See [Configuration](configuration.md#bus-registration).

## Connection

Point the transport at the broker with `Host`. The connection string is typically injected via
Aspire or configuration:

```csharp
// Via Aspire (automatic) — registers the RabbitMQ client connection
builder.AddRabbitMQClient("rabbitmq");

// Via connection string
transport.Host("amqp://guest:guest@localhost:5672/");
```

Credentials can also be supplied (and overridden) through the host configurator rather than embedded
in the URI — useful when the username/password come from a secret store:

```csharp
transport.Host("amqp://broker.internal:5672/", h =>
{
    h.Username(username);
    h.Password(password);   // never logged or echoed in diagnostics
});
```

## TLS and mutual TLS

For an encrypted connection, use the `amqps://` scheme and configure TLS through the host
configurator's `UseTls` block. `UseTls` exposes an `ITlsConfigurator`:

| Method | Meaning |
|--------|---------|
| `WithCertificate(path, password?)` | Client certificate file (PFX or PEM) and an optional private-key passphrase (the passphrase is never logged). |
| `WithMutualAuthentication()` | Enables mTLS — the client presents its certificate to the broker during the handshake. |
| `WithServerValidation(SslPolicyErrors)` | The set of `SslPolicyErrors` tolerated during server-certificate validation. Defaults to `SslPolicyErrors.None` (strict). **In production always use `None`** — relax it only to accept self-signed certificates in test environments. |

```csharp
using System.Net.Security;

// Server-authenticated TLS
transport.Host("amqps://broker.internal:5671/", h =>
{
    h.Username(username);
    h.Password(password);
    h.UseTls(tls => tls.WithServerValidation(SslPolicyErrors.None));
});

// Mutual TLS (client certificate)
transport.Host("amqps://broker.internal:5671/", h =>
{
    h.UseTls(tls => tls
        .WithCertificate("/etc/secrets/client.pfx", certPassword)
        .WithMutualAuthentication()
        .WithServerValidation(SslPolicyErrors.None));
});
```

## Receive endpoints

Each receive endpoint binds a consumer (or several) to a queue and carries its own concurrency and
retry settings:

```csharp
transport.ReceiveEndpoint("orders", e =>
{
    e.PrefetchCount = 16;            // broker-level prefetch
    e.ConcurrentMessageLimit = 8;    // in-flight concurrency
    e.RetryCount = 3;                // retry attempts before DLQ
    e.RetryInterval = TimeSpan.FromSeconds(1);

    e.Consumer<OrderConsumer, OrderCreated>();
});
```

An endpoint can also host a raw consumer (`e.RawConsumer<T>()`), a saga state machine
(`e.StateMachineSaga<T>()`), multiple typed consumers, consume-time routing-key dispatch, and
per-key ordering. See [Publishing and Consuming](publishing-and-consuming.md),
[Consumer Routing Keys](consumer-routing-keys.md), and
[Per-Key Consumer Ordering](per-key-ordering.md).

## Settlement and dead-lettering

When a consumer succeeds the message is acknowledged; when it throws, BareWire retries up to
`RetryCount` and then negatively settles the delivery. With a dead-letter exchange declared on the
queue, the failed message is routed to the DLX rather than discarded — without one, a rejected
message is permanently lost (and BareWire logs a warning). Always configure a DLX on production
queues. See [Retry and Dead Letter Queues](retry-and-dlq.md).

## Routing semantics

A publisher confirm tells you the broker **accepted** a publication — not that it **routed** it to a
queue. By default BareWire publishes with the AMQP `mandatory` flag off, so a message the broker
accepts but cannot route to any queue (topology drift, a missing binding or queue, a wrong routing
key) is silently dropped while `SendResult.IsConfirmed` still reports `true`. This is at-most-once
routing: fast, but a misconfigured topology loses messages without a signal.

Enable **guaranteed routing** to surface unroutable publications instead of dropping them silently:

```csharp
wire.UseRabbitMq(rmq =>
{
    rmq.GuaranteedRouting();   // opt-in; default is off
    // ... host, topology, endpoints ...
});
```

With it enabled, BareWire publishes `mandatory` and detects an unroutable message via the broker's
return, mapping it to `SendResult.IsConfirmed == false` (and logging a warning). The default-off
behaviour is unchanged — routable publishes are unaffected (the channel already awaits a per-message
publisher confirm, so there is no extra round-trip), and there is no per-message allocation on the
send path.

**Where it fails closed.** The negative confirm only changes the outcome for code that inspects
`SendResult` — chiefly the [transactional outbox](outbox.md) dispatcher, which treats
`IsConfirmed == false` as non-delivery and retries (the row stays claimed instead of being marked
delivered). The direct `IBus.PublishAsync` / `ISendEndpoint.SendAsync` path is **fire-and-forget**:
the caller never receives a `SendResult`, and the background publisher does not redeliver on a
negative confirm. On that path guaranteed routing turns a *silent* drop into an *observable* one (the
warning log) but does not by itself make direct publishing at-least-once. For at-least-once delivery
against topology drift, publish through the outbox with this option enabled.

## Feature map

RabbitMQ-specific behaviour is documented across these guides:

| Topic | Guide |
|-------|-------|
| Transport registration paths and layering | [Configuration](configuration.md) |
| Exchanges, queues, bindings, `IQueueConfigurator`, DLX | [Topology](topology.md) |
| Publish/subscribe, request/response, raw messages, per-type send routing | [Publishing and Consuming](publishing-and-consuming.md) |
| Consume-time routing-key dispatch on a shared queue | [Consumer Routing Keys](consumer-routing-keys.md) |
| Single-active-consumer / consistent-hash per-key ordering | [Per-Key Consumer Ordering](per-key-ordering.md) |
| Retry policies and dead-letter exchanges | [Retry and Dead Letter Queues](retry-and-dlq.md) |
| Publish-style competing responders | [Publishing and Consuming](publishing-and-consuming.md#publish-style-competing-responders-first-in-wins) |
| At-most-once vs opt-in guaranteed routing | [Routing semantics](#routing-semantics) |

## See also

- [Configuration](configuration.md)
- [Topology](topology.md)
- [Transports](transports.md) — the transport-agnostic overview and the other adapters
