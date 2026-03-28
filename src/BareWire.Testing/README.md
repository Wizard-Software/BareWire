# BareWire.Testing

In-memory test harness for BareWire with deterministic message delivery and assertion helpers.

## Installation

```bash
dotnet add package BareWire.Testing
```

## Usage

```csharp
[Fact]
public async Task OrderCreated_StartsOrderSaga()
{
    await using var harness = new BareWireTestHarness();
    harness.AddConsumer<OrderCreatedConsumer>();

    await harness.Start();
    await harness.Bus.Publish(new OrderCreated(Guid.NewGuid()));

    (await harness.Consumed<OrderCreated>()).Should().HaveCount(1);
}
```

## Features

- In-memory transport for fast, isolated tests
- Deterministic message delivery (no timing issues)
- Built-in assertion helpers with AwesomeAssertions
- Consumer and saga test support

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
