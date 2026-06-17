# BareWire.Transport.AzureServiceBus

Azure Service Bus transport provider for BareWire with PeekLock settlement, native dead-letter queue, and native deduplication.

## Installation

```bash
dotnet add package BareWire.Transport.AzureServiceBus
```

## Usage

```csharp
builder.Services.AddBareWireAzureServiceBus(asb =>
{
    asb.ConnectionString("Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...");
    asb.PrefetchCount(0);          // default 0 (safe with PeekLock lock-duration)
    asb.MaxConcurrentCalls(1);     // reserved — single-reader in R2.1

    // Sessions (R2.2) — opt-in, ordered FIFO processing per SessionId
    asb.UseSessions(maxConcurrentSessions: 4);     // enables sessions; bounds concurrent sessions
    asb.MaxAutoLockRenewDuration(TimeSpan.FromMinutes(5)); // background session-lock renew budget (default 5 min)
});
```

> The connection string contains a SAS `SharedAccessKey` (a secret). It is never logged, never included in `ToString()`, and never echoed in exception messages.

## Sessions (R2.2)

Azure Service Bus sessions provide **FIFO ordering per `SessionId`**. BareWire maps a session per correlation/saga instance and processes each session's messages in order.

- **Opt-in** via `asb.UseSessions(maxConcurrentSessions)`. The queue itself must be created with `RequiresSession = true` — declare it with the topology argument `bw.asb.requires-session = true` (Azure Service Bus does not allow toggling this after the queue exists).
- **Produce path** — `ServiceBusMessage.SessionId` is set from the explicit `BW-SessionId` header when present, otherwise from the canonical `correlation-id` header (so all messages of one saga instance — same `CorrelationId` — land in the same session). When neither is present the message is sent without a session (R2.1 behaviour).
- **Consume path** — each accepted session is read sequentially by a single reader into its **own** bounded channel (`SingleWriter = true`), preserving per-`SessionId` FIFO. `MaxConcurrentSessions` bounds how many sessions are processed in parallel (an accept-side semaphore caps concurrent session tasks + channels). The session path pins channel back-pressure to **Wait** mode — `Drop*` flow-control modes would create a mid-session FIFO gap and are therefore not honoured when sessions are enabled.
- **Session-lock under back-pressure** — while a session's messages wait in the bounded channel, a background task renews the session lock via `RenewSessionLockAsync` (interval derived from `SessionLockedUntil`), bounded by `MaxAutoLockRenewDuration`. This prevents `SessionLockLost` (and the loss/replay of the whole session) when the reader is blocked by back-pressure. A reactive `SessionLockLost` catch + back-off remains as a safety net.
- **Settlement** — `Complete`/`Abandon`/`DeadLetter`/`Defer` execute on the session receiver (which holds the session lock), via the same unchanged settlement router as the non-session path.
- **SAGA integration** — the same `CorrelationId` → same `SessionId` → joint FIFO processing per saga (mechanism only; full session-state persistence of saga machine state is out of scope — see **ADR-011**). The transport depends on `BareWire.Abstractions` only; the SAGA bridge is a header convention, never a project reference.
- **Security note** — a session is an **ordering** boundary, **not** an isolation/authorization boundary. `SessionId` derives from an unauthenticated header (raw-first); cross-session injection ("session squatting") is a known, accepted risk. Tenant isolation depends on SAS/Entra authorization (R2.4), not on sessions. See **ADR-011**.

Full FIFO behaviour (real broker, end-to-end ordering, session-lock renewal under load) is validated by the integration tests in **R2.5**; R2.2 ships broker-free unit tests for the pure mapping, options, topology, channel-ordering and accept-gate invariants.

## Features

- `ITransportAdapter` over `Azure.Messaging.ServiceBus` 7.x
- PeekLock settlement — `Complete` / `Abandon` / `DeadLetter` / `Defer` mapped from BareWire `SettlementAction` (`Ack` → Complete, `Nack`/`Requeue` → Abandon, `Reject` → DeadLetter, `Defer` → Defer)
- Native dead-letter queue (`DlqNative`) and native deduplication (`NativeDeduplication`)
- `PrefetchCount` mapping onto the receiver
- Consumer streaming via `ServiceBusReceiver` (PeekLock) → bounded channel with credit-based flow control (ADR-004)
- Zero-copy body path — `BinaryData` wraps `ReadOnlyMemory<byte>` on publish and `ReadOnlySequence<byte>` on consume without an extra allocation
- Idempotent topology — queues are created via `ServiceBusAdministrationClient` (`MessagingEntityAlreadyExists` is swallowed); Azure Service Bus has no exchange/binding concept, so those declarations are skipped. Queue parameters use the `bw.asb.*` argument convention (`bw.asb.max-delivery-count`, `bw.asb.lock-duration`, `bw.asb.requires-duplicate-detection`, `bw.asb.requires-session`).
- Sessions (R2.2) — opt-in FIFO ordering per `SessionId` with per-session bounded channels, accept-side concurrency bound, and background session-lock renewal (see the **Sessions** section below)
- Manual topology by default (ADR-002)

## Capabilities

`NativeDeduplication | Sessions | NativeScheduling | DlqNative`

> **Note:** `Sessions` (ordered processing per `SessionId`) is implemented as of **R2.2** (see the **Sessions** section above; full end-to-end FIFO is validated by the R2.5 integration tests). `NativeScheduling` (native scheduled messages) arrives in **R2.3**. Authentication beyond a SAS connection string (Entra ID / `DefaultAzureCredential` with token refresh) arrives in **R2.4**.

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
