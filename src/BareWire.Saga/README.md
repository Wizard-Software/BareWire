# BareWire.Saga

SAGA state machine for BareWire with deterministic scheduling and correlation support.

## Installation

```bash
dotnet add package BareWire.Saga
```

## Usage

```csharp
public sealed class OrderSaga : BareWireSaga<OrderSagaState>,
    IConsumer<OrderCreated>,
    IConsumer<PaymentCompleted>
{
    public async Task Consume(ConsumeContext<OrderCreated> context)
    {
        State.OrderId = context.Message.OrderId;
        Schedule(TimeSpan.FromMinutes(30), new OrderTimeout(State.OrderId));
    }

    public async Task Consume(ConsumeContext<PaymentCompleted> context)
    {
        State.IsPaid = true;
        Complete();
    }
}
```

## Features

- Deterministic scheduling with persistent timers
- Correlation by message property
- State persistence via pluggable providers (EF Core, etc.)

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
