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
});
```

> The connection string contains a SAS `SharedAccessKey` (a secret). It is never logged, never included in `ToString()`, and never echoed in exception messages.

## Features

- `ITransportAdapter` over `Azure.Messaging.ServiceBus` 7.x
- PeekLock settlement — `Complete` / `Abandon` / `DeadLetter` / `Defer` mapped from BareWire `SettlementAction` (`Ack` → Complete, `Nack`/`Requeue` → Abandon, `Reject` → DeadLetter, `Defer` → Defer)
- Native dead-letter queue (`DlqNative`) and native deduplication (`NativeDeduplication`)
- `PrefetchCount` mapping onto the receiver
- Consumer streaming via `ServiceBusReceiver` (PeekLock) → bounded channel with credit-based flow control (ADR-004)
- Zero-copy body path — `BinaryData` wraps `ReadOnlyMemory<byte>` on publish and `ReadOnlySequence<byte>` on consume without an extra allocation
- Idempotent topology — queues are created via `ServiceBusAdministrationClient` (`MessagingEntityAlreadyExists` is swallowed); Azure Service Bus has no exchange/binding concept, so those declarations are skipped. Queue parameters use the `bw.asb.*` argument convention (`bw.asb.max-delivery-count`, `bw.asb.lock-duration`, `bw.asb.requires-duplicate-detection`).
- Manual topology by default (ADR-002)

## Capabilities

`NativeDeduplication | Sessions | NativeScheduling | DlqNative`

> **Note:** `Sessions` and `NativeScheduling` are declared as transport capabilities, but their full implementation is delivered in later roadmap tasks — sessions (ordered processing per `SessionId`) in **R2.2** and native scheduled messages in **R2.3**. Authentication beyond a SAS connection string (Entra ID / `DefaultAzureCredential` with token refresh) arrives in **R2.4**.

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
