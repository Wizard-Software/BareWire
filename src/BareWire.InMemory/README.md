# BareWire.InMemory

Single-call registration bundle for [BareWire](https://barewire.wizardsoftware.pl) with the
**in-memory** transport.

This package depends on **both** the BareWire core (`BareWire`) and the in-memory transport
(`BareWire.Transport.InMemory`) and exposes one convenience method that registers them together
in a single call.

## Install

```bash
dotnet add package BareWire.InMemory
```

## Usage — single call

```csharp
builder.Services.AddBareWireJsonSerializer();
builder.Services.AddBareWireWithInMemory(
    transport =>
    {
        transport.DefaultExchange("");
        transport.AutoDeclareEndpointQueues();
        transport.ReceiveEndpoint("orders", e => e.Consumer<OrderConsumer, OrderCreated>());
    });
```

The optional `bus` delegate may be omitted when you only need transport defaults:

```csharp
builder.Services.AddBareWireWithInMemory(transport => transport.DefaultExchange(""));
```

## Delivery guarantee

The in-memory transport is **explicitly at-most-once**. Every queued and in-flight message lives
in process memory only — a process restart or crash discards them unconditionally, with no
persistence and no recovery on the next start. It is meant for local development, integration
tests, and any deployment where a single process is acceptable and an external broker would be
pure overhead. When at-least-once delivery is required, register a broker-backed transport
instead (see "Switching to RabbitMQ" below).

The transactional outbox dispatcher retries a message until the in-memory transport *admits* it
into a queue — that retry only covers admission, not delivery. A message the queue has already
accepted is still lost on a process restart or crash, so the outbox does not turn this transport
into an at-least-once one on its own; it is a retry mechanism layered on top of a transport, not a
substitute for a broker.

See the "Trust boundary" section of the
[`BareWire.Transport.InMemory` README](https://www.nuget.org/packages/BareWire.Transport.InMemory)
for the isolation model: every publisher and consumer wired through this bundle shares the trust
boundary of the hosting process.

## Sizing

Queue capacity, memory bounds, and publisher-isolation behavior under a stalled consumer are the
same as the underlying transport. See the "Sizing" section of the
[`BareWire.Transport.InMemory` README](https://www.nuget.org/packages/BareWire.Transport.InMemory)
for the full guidance and formulas.

## Switching to RabbitMQ

Switching from the in-memory transport to RabbitMQ is a registration change, not a code change —
topology declarations and consumer code are shared between both:

```csharp
static void ConfigureTopology(ITopologyConfigurator topology)
{
    topology.DeclareExchange("orders", ExchangeType.Topic);
    topology.DeclareQueue("order-processing");
    topology.BindExchangeToQueue("orders", "order-processing", "order.*");
}
```

```csharp
// In-memory (development)
builder.Services.AddBareWireWithInMemory(
    transport => transport.ConfigureTopology(ConfigureTopology));

// RabbitMQ (production)
builder.Services.AddBareWireWithRabbitMq(
    transport =>
    {
        transport.Host("localhost");
        transport.ConfigureTopology(ConfigureTopology);
    });
```

Use `amqps://` (or `ConfigureTls` on the RabbitMQ transport options) instead of a plain `amqp://`
host in production, so traffic to the broker is encrypted in transit.

## Equivalent two-call registration

`AddBareWireWithInMemory` is sugar over the explicit two-call form, which remains fully supported
(use it when you need to register multiple transports, or want the core and transport packages
referenced separately):

```csharp
builder.Services.AddBareWireInMemory(transport => transport.DefaultExchange(""));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

## One bus per container

Call `AddBareWireWithInMemory` at most once per `IServiceCollection`. It rejects, before
registering anything, a container that already has the core bus, a transport adapter (including
one registered by a prior `AddBareWireInMemory` call, or by a different transport), or the
internal bus-shutdown options — each of those raises a configuration exception naming what was
found and pointing at the single-call or two-call registration path. This guard only detects
registrations made *before* `AddBareWireWithInMemory` runs; it cannot detect a plain
`AddBareWire(...)` call made afterwards on the same collection.

The `DrainTimeout` passed to the transport configurator is forwarded to the core's shutdown
coordination, which bounds how long stopping the bus waits for in-flight work to settle. In
practice that value is further bounded by the hosting process's own shutdown budget (for example
`HostOptions.ShutdownTimeout` under the .NET generic host) — a drain timeout longer than the
host's shutdown budget cannot be honored in full.

## Layering

The bundle is a thin composition layer over `BareWire` + `BareWire.Transport.InMemory`. The core
never depends on a transport and a transport never depends on the core — the bundle is a separate
layer that references both, preserving the one-directional dependency rule.

See the [BareWire documentation](https://barewire.wizardsoftware.pl) for the full registration
and configuration guide.
