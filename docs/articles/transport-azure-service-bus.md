# Azure Service Bus Transport

The Azure Service Bus transport runs BareWire over `Azure.Messaging.ServiceBus`. It uses
PeekLock settlement, a native dead-letter queue, native deduplication, native sessions
(FIFO ordering per `SessionId`), and native scheduled delivery. Like every BareWire transport it
ships as two packages — the transport itself (`BareWire.Transport.AzureServiceBus`) and a thin
bundle (`BareWire.AzureServiceBus`) that wires the transport and the core engine together in one
call. Topology is manual by default — you declare your queues explicitly.

## Install

```bash
dotnet add package BareWire.AzureServiceBus            # bundle (core + transport)
dotnet add package BareWire.Transport.AzureServiceBus  # or the transport alone
```

## Registration

### Single call — bundle (recommended)

`AddBareWireWithAzureServiceBus` registers the transport adapter and the core engine together.
The `transport` delegate configures the Azure Service Bus connection and options; the optional
`bus` delegate configures consumers, middleware, and serializers.

```csharp
builder.Services.AddBareWireWithAzureServiceBus(
    transport => transport.ConnectionString(connectionString),
    bus =>
    {
        bus.AddConsumer<OrderConsumer>();
        // endpoints, middleware, serializers...
    });
```

Omit the `bus` delegate when transport defaults are enough:

```csharp
builder.Services.AddBareWireWithAzureServiceBus(
    transport => transport.ConnectionString(connectionString));
```

### Two calls — transport and core registered separately

`AddBareWireWithAzureServiceBus` is sugar over the explicit two-call form, which remains fully
supported. Use it when you reference the core and transport packages separately, or when an
application registers more than one transport:

```csharp
builder.Services.AddBareWireAzureServiceBus(transport => transport.ConnectionString(connectionString));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

The bundle is a thin composition layer over the core (`BareWire`) and the transport — the core
never depends on a transport and vice versa. See [Configuration](configuration.md) for the rationale.

## Authentication

The transport supports two authentication modes.

### SAS (Shared Access Signature) — default

Authenticate with a connection string that carries a SAS key. Suitable for local development
and environments without a Managed Identity.

```csharp
builder.Services.AddBareWireAzureServiceBus(asb =>
{
    asb.UseSasAuth("Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=...");
});
```

`ConnectionString(...)` is a legacy alias for `UseSasAuth(...)` — both set SAS mode and are
interchangeable.

### Entra ID (Azure RBAC / Managed Identity)

Authenticate with a `TokenCredential` against the fully-qualified namespace host. Recommended for
production workloads using Managed Identity or Azure RBAC.

```csharp
using Azure.Identity;

builder.Services.AddBareWireAzureServiceBus(asb =>
{
    asb.UseEntraIdAuth("myns.servicebus.windows.net", new DefaultAzureCredential());
});
```

The Azure SDK refreshes the token automatically — BareWire does not run its own refresh loop.

> The connection string contains a SAS `SharedAccessKey` (a secret), and the `TokenCredential` is
> a live credential object. Neither is ever logged, included in `ToString()`, or echoed in
> exception messages. Only the namespace host (a non-secret identifier) appears in diagnostic
> output.

## Sessions

Azure Service Bus sessions provide FIFO ordering per `SessionId`. Enable them with `UseSessions`,
which also bounds how many sessions are accepted and processed concurrently:

```csharp
builder.Services.AddBareWireAzureServiceBus(asb =>
{
    asb.UseSasAuth(connectionString);
    asb.UseSessions(maxConcurrentSessions: 4);              // opt in; bound concurrent sessions
    asb.MaxAutoLockRenewDuration(TimeSpan.FromMinutes(5));  // background session-lock renew budget
    asb.SessionIdleTimeout(TimeSpan.FromSeconds(30));       // release an idle session
});
```

Key points:

- **Queue requirement** — the target queue must have been created with `RequiresSession = true`.
  Declare it with the `bw.asb.requires-session = true` queue argument (Azure Service Bus cannot
  toggle this after the queue exists). See [Topology](topology.md).
- **Routing** — on the produce path the `SessionId` is taken from the explicit `BW-SessionId`
  header when present, otherwise from the canonical `correlation-id` header, so all messages of one
  correlation land in the same session.
- **Ordering** — each accepted session is read sequentially into its own bounded channel,
  preserving per-`SessionId` FIFO. `maxConcurrentSessions` caps how many sessions run in parallel.
- **Lock auto-renewal** — while a session's messages wait under back-pressure, a background task
  renews the session lock at intervals derived from the remaining lock window, bounded by
  `MaxAutoLockRenewDuration` (default 5 minutes). Set it to `TimeSpan.Zero` to disable renewal.

> A session is an ordering boundary, not an authorization boundary. Tenant isolation depends on
> your SAS or Entra ID authorization, not on `SessionId`.

## Scheduled Messages

The transport exposes Azure Service Bus native scheduled delivery through BareWire's native message
scheduler capability: a message can be scheduled for a future enqueue time and later cancelled
before it is delivered. The broker holds the message until its scheduled time — no in-process timer
is involved, and the body is wrapped without an extra copy on the way out.

## Transport Options

Configure these on `IAzureServiceBusConfigurator` inside the `transport` / `asb` delegate:

| Method | Default | Meaning |
|--------|---------|---------|
| `UseSasAuth(string)` | — | SAS authentication via connection string. |
| `ConnectionString(string)` | — | Legacy alias for `UseSasAuth`. |
| `UseEntraIdAuth(string, TokenCredential)` | — | Entra ID authentication against the namespace host. |
| `PrefetchCount(int)` | `0` | Messages pre-fetched into a local buffer. `0` is safest for PeekLock — pre-fetched messages start their lock timer immediately. |
| `MaxConcurrentCalls(int)` | `1` | Maximum messages processed concurrently per consumer. |
| `UseSessions(int)` | off | Enables FIFO-per-`SessionId` processing; the argument bounds concurrent sessions (default `1`). |
| `SessionIdleTimeout(TimeSpan)` | SDK default (~1 s) | Idle time before a session is released. Must be positive. |
| `MaxAutoLockRenewDuration(TimeSpan)` | `5 minutes` | Total budget for background session-lock renewal. `TimeSpan.Zero` disables it. |

Settlement maps BareWire's settlement actions onto PeekLock dispositions — acknowledge completes
the message, requeue abandons it, reject dead-letters it, and defer defers it. Queues are created
idempotently and configured through the `bw.asb.*` argument convention (`bw.asb.max-delivery-count`,
`bw.asb.lock-duration`, `bw.asb.requires-duplicate-detection`, `bw.asb.requires-session`); Azure
Service Bus has no exchange/binding concept, so those declarations are skipped.

## See also

- [API reference](../api/index.md)
- [Configuration](configuration.md)
- [Topology](topology.md)
