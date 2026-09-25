# BareWire.Testing

An in-process test harness that wires up a fully working BareWire bus backed by the real in-memory
transport, so tests can publish and send messages and observe what reaches the transport without an
external broker.

## Installation

```bash
dotnet add package BareWire.Testing
```

## Usage

```csharp
[Fact]
public async Task PublishAsync_OrderCreated_ReachesTransport()
{
    await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync();

    Task<OutboundMessage> waitTask = harness.WaitForPublishAsync<OrderCreated>(TimeSpan.FromSeconds(5));

    await harness.Bus.PublishAsync(new OrderCreated(Guid.NewGuid()));

    OutboundMessage message = await waitTask;
    message.RoutingKey.Should().Be(typeof(OrderCreated).FullName);
}
```

`CreateAsync` starts the bus; disposing the harness (`await using`, or an explicit `DisposeAsync()`
call) stops it. `WaitForPublishAsync<T>` and `WaitForSendAsync<T>` resolve as soon as a message whose
routing key matches `T` is observed on the transport — no polling required — or throw
`TimeoutException` once the given timeout elapses.

An optional `configure` callback exposes the same `IBusConfigurator` used in production (middleware,
receive endpoints, per-type serializer mappings via `MapSerializer<,>()`), so a harness can exercise
consumers and sagas the same way a hosted bus would.

## How it works

Each harness builds its own private dependency-injection container and registers the real in-memory
transport into it — no two harness instances share a broker, a queue, or any other state, so tests can
run in parallel without interfering with each other.

The harness registers the in-memory transport in a compatibility mode: a default exchange of `""`
plus automatic declaration of receive-endpoint queues, matching how a plain `PublishAsync`/`SendAsync`
call behaved before the harness used the real transport. A message published with no matching queue is
accepted and then dropped with a warning in the transport's own logs (the harness itself produces no
log output by default) — it is still observable through `WaitForPublishAsync`/`WaitForSendAsync`,
which watch every outbound send regardless of whether it was ultimately delivered anywhere.

A message scheduled for native delivery (for example a saga timeout) is delivered directly by the
transport's own scheduler once it fires, bypassing the harness's send observation entirely — such a
message is never seen by `WaitForPublishAsync`/`WaitForSendAsync`, only by a real consumer or a direct
read of the transport's queue.

The harness's default serializer does not actually serialize message content — tests that publish a
message typically only need it to round-trip through the transport by type name, not by byte-for-byte
payload. Map a real serializer for specific message types via `configure`'s `MapSerializer<,>()` when a
test needs one.

Disposing the harness stops the bus the same way a production shutdown does: it waits for in-flight
work to settle before consumer loops are cancelled, bounded by the configured drain timeout (10 seconds
by default). A test that leaves an active consumer with a non-empty queue at the point of disposal can
therefore stretch `DisposeAsync` out to that timeout instead of returning immediately.

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
