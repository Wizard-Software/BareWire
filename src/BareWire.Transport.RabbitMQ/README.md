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
- Opt-in guaranteed routing (`GuaranteedRouting()`) — publishes `mandatory` and maps an unroutable message to `SendResult.IsConfirmed:false` (acted on by the outbox dispatcher for at-least-once; the direct fire-and-forget path logs a warning but does not redeliver); default off, bit-identical to before

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
