# BareWire.Transport.RabbitMQ

RabbitMQ transport provider for BareWire with manual topology control and connection pooling.

## Installation

```bash
dotnet add package BareWire.Transport.RabbitMQ
```

## Usage

```csharp
builder.AddBareWire(wire =>
{
    wire.UseRabbitMq(rmq =>
    {
        rmq.Host("localhost");
        rmq.ConfigureTopology(t =>
        {
            t.DeclareExchange("orders", ExchangeType.Topic);
            t.DeclareQueue("order-processing");
            t.Bind("orders", "order-processing", "order.created");
        });
    });
});
```

## Features

- Manual topology by default (`ConfigureConsumeTopology = false`)
- Connection pooling with configurable channel limits
- TLS/mTLS support
- Auto-topology available as opt-in

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
