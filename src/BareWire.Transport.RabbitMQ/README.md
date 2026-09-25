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
- Reserved `BW-*` header trust boundary — an unmapped `BW-*` header is never sent on the wire and is dropped on receive (case-insensitive). Routing metadata (`BW-Exchange`, `BW-RoutingKey`, `BW-ConsumerChannelId`) is always stamped by the transport from the delivery itself; the message type, `message-id`, `correlation-id`, `reply-to`, `content-type` and `traceparent` remain under the publisher's control — restrict untrusted publishers with broker permissions or an authorization middleware rather than relying on the header filter alone. Carry your own `BW-*` header through the broker with an explicit mapping, e.g. `headers.MapHeader("BW-TenantId", "BW-TenantId")`. **Behavior change:** an unmapped raw `BW-*` header a publisher sets no longer reaches consumers — map it explicitly to keep receiving it; for the message type, set the AMQP `type` property or configure `MapMessageType(...)` (a raw `BW-MessageType` header keeps working only while `type` is empty)

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
