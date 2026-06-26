# Publishing and Consuming

## Publish/Subscribe (Fan-out)

The most common pattern — publish an event and all subscribed consumers receive it.

### Publishing

Inject `IPublishEndpoint` or `IBus` and call `PublishAsync`:

```csharp
app.MapPost("/messages", async (
    MessageRequest request,
    IPublishEndpoint bus,
    CancellationToken ct) =>
{
    var message = new MessageSent(Guid.NewGuid(), request.Content, DateTime.UtcNow);
    await bus.PublishAsync(message, ct);
    return Results.Accepted(value: new { message.Id });
});
```

Use a fanout exchange so all bound queues receive every message:

```csharp
topology.DeclareExchange("messages", ExchangeType.Fanout, durable: true);
topology.DeclareQueue("messages", durable: true);
topology.BindExchangeToQueue("messages", "messages");
```

> See: `samples/BareWire.Samples.BasicPublishConsume/Program.cs`

### Consuming

Implement `IConsumer<T>`:

```csharp
public sealed class MessageConsumer : IConsumer<MessageSent>
{
    public async Task ConsumeAsync(ConsumeContext<MessageSent> context)
    {
        var msg = context.Message;
        // process the message
    }
}
```

Register on a receive endpoint:

```csharp
rmq.ReceiveEndpoint("messages", e =>
{
    e.Consumer<MessageConsumer, MessageSent>();
});
```

### Publishing from a Consumer

Use `context.PublishAsync()` to publish follow-up events from within a consumer:

```csharp
public async Task ConsumeAsync(ConsumeContext<OrderCreated> context)
{
    // process order...
    await context.PublishAsync(new OrderProcessed(context.Message.OrderId));
}
```

> See: `samples/BareWire.Samples.RabbitMQ/Consumers/OrderConsumer.cs`

## Request-Response

For synchronous request-response messaging, use `IRequestClient<T>`:

### Sending a Request

```csharp
app.MapPost("/validate-order", async (
    OrderValidationRequest request,
    IBus bus,
    CancellationToken ct) =>
{
    var client = bus.CreateRequestClient<ValidateOrder>();

    try
    {
        var response = await client.GetResponseAsync<OrderValidationResult>(
            new ValidateOrder(request.OrderId, request.Items, request.TotalAmount),
            ct);

        return Results.Ok(response.Message);
    }
    catch (RequestTimeoutException)
    {
        return Results.StatusCode(504);
    }
});
```

### Responding

The consumer calls `context.RespondAsync()`:

```csharp
public sealed class OrderValidationConsumer : IConsumer<ValidateOrder>
{
    public async Task ConsumeAsync(ConsumeContext<ValidateOrder> context)
    {
        var result = new OrderValidationResult(
            context.Message.OrderId,
            IsValid: true,
            Reason: "All checks passed");

        await context.RespondAsync(result);
    }
}
```

Use a direct exchange for point-to-point routing:

```csharp
topology.DeclareExchange("order-validation", ExchangeType.Direct, durable: true);
topology.DeclareQueue("order-validation", durable: true);
topology.BindExchangeToQueue("order-validation", "order-validation", routingKey: "");
```

> See: `samples/BareWire.Samples.RequestResponse/`

### Publish-Style (Competing Responders, First-In-Wins)

By default request-response is **send-style**: the requester resolves a fixed
responder queue once (at `CreateRequestClientAsync<T>`) and must *know* that
queue. **Publish-style** mode (opt-in, per request type) decouples this: the
request is **published** to a per-type fanout exchange named `Namespace:TypeName`,
and responders bind their queues to that exchange. Multiple responders (different
versions or deployments) can answer the same request type — the **first response
wins (first-in-wins)** and the rest are silently dropped. This unblocks
blue-green / canary / A/B migrations without reconfiguring requesters. It is
**off by default**: without `PublishRequest<T>()` the request-response behaviour
is byte-identical (non-breaking), and the reply path and correlation are
unchanged.

```csharp
services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(rmq =>
    {
        rmq.Host("amqp://localhost");

        // Manual topology: per-type fanout exchange + responder queue bindings.
        // The exchange name follows the MassTransit convention: a literal colon
        // between namespace and type name — "Namespace:TypeName".
        rmq.ConfigureTopology(t =>
        {
            t.DeclareExchange("OrderSystem.Contracts:CheckOrderStatus",
                ExchangeType.Fanout, durable: true, autoDelete: false);

            // Two competing responder queues (e.g. v1 and v2) bound to the
            // same exchange (fanout ignores the routing key):
            t.DeclareQueue("order-status-v1", durable: true);
            t.DeclareQueue("order-status-v2", durable: true);
            t.BindExchangeToQueue("OrderSystem.Contracts:CheckOrderStatus", "order-status-v1", routingKey: "");
            t.BindExchangeToQueue("OrderSystem.Contracts:CheckOrderStatus", "order-status-v2", routingKey: "");
        });

        // Enable publish-style for the request type (read at CreateRequestClientAsync<T>).
        rmq.PublishRequest<CheckOrderStatus>();
    });
});

// The requester is unchanged — it never knows the responder queue name, only the
// request type. The first response (from v1 or v2) wins; the loser arrives on the
// server-named reply queue, finds no pending entry, is acked (autoAck) and silently
// dropped — with no warn-spam.
var client = await bus.CreateRequestClientAsync<CheckOrderStatus>();
var response = await client.GetResponseAsync<OrderStatusResult>(
    new CheckOrderStatus(orderId));
```

#### Options: exchange-name override / strict / auto-declare

When a responder uses a non-default entity name, or you want fast "no responder
bound" diagnostics, or auto-declaration of the exchange, pass the options block.
There is **no** bare `PublishRequest<T>(string)` overload — the name override is
always set through `o.ExchangeName`:

```csharp
rmq.PublishRequest<CheckOrderStatus>(o =>
{
    o.ExchangeName = "Orders.Api:CheckOrderStatus"; // override the default name formatter
    o.Strict       = true;                          // mandatory:true → fast "no responder bound"
    o.AutoDeclare  = true;                          // auto-declare the per-type fanout exchange
});
```

The default name formatter follows the MassTransit v8 convention: the full type
name with a **literal colon** between namespace and type name (`Namespace:TypeName`,
PascalCase, durable, auto-delete=false). The colon must be **exact** — a `.`
instead of `:`, kebab-case, or lowercase silently breaks interop (BareWire
publishes to an exchange the responder is not listening on, and the request
times out).

#### Caveats

Three caveats matter before turning competing-responders on:

1. **Correlation echo in the emitted destination address.** Under publish-style
   the emitted destination address changes to the per-type fanout exchange URI.
   It is **diagnostic only** — response routing still relies on the reply
   address, never on the destination address — and carries no correlation value
   or PII.
2. **The reply-queue fan-out is outside credit-based flow control.** The reply
   queue is consumed with `autoAck: true`, so the fan-out of N responses (one per
   competing responder) is not bounded by the flow-control mechanism. The cost
   grows linearly with the number of responders and is bounded operationally —
   by how many responders you bind to the exchange.
3. **First-in-wins drops N-1 RESPONSES, not N-1 EXECUTIONS.** Every competing
   responder **executes** the full request handler (side effects included — DB
   writes, e-mails); only its *response* is discarded. This is a footgun for
   responders with side effects — in competing-responders mode those effects run
   N times. The pattern is safe for idempotent / read-only responders (the
   typical "query" request-response).

> **Generic / nested request types.** The default formatter maps the
> `Namespace:TypeName` convention for **simple types** only. A generic or nested
> request type needs an explicit `o.ExchangeName` override.

## Raw Message Consumption

For interoperability with legacy systems that don't use BareWire's serialization, use `IRawConsumer`:

### Raw Consumer

```csharp
public sealed class RawEventConsumer : IRawConsumer
{
    public async Task ConsumeAsync(RawConsumeContext context)
    {
        // Access raw bytes and headers
        var body = context.Body;
        var headers = context.Headers;
        var sourceSystem = context.Headers["SourceSystem"];

        // Try manual deserialization
        if (context.TryDeserialize<ExternalEvent>(out var evt))
        {
            // process typed event
        }
    }
}
```

Register with `RawConsumer<T>()`:

```csharp
rmq.ReceiveEndpoint("raw-events", e =>
{
    e.RawConsumer<RawEventConsumer>();
});
```

### Custom Header Mapping

Map non-standard headers from external systems to BareWire conventions:

```csharp
rmq.ConfigureHeaderMapping(headers =>
{
    headers.MapCorrelationId("X-Correlation-Id");
    headers.MapMessageType("X-Message-Type");
    headers.MapHeader("SourceSystem", "X-Source-System");
});
```

> See: `samples/BareWire.Samples.RawMessageInterop/`

## Publishing with Custom Headers

The `PublishAsync` overload with a `headers` parameter lets you attach additional transport headers to the outbound message:

```csharp
var headers = new Dictionary<string, string>
{
    ["message-id"] = originalMessageId.ToString(),
    ["X-Source"] = "redelivery-endpoint"
};

await bus.PublishAsync(message, headers, ct);
```

The `"message-id"` key has special meaning — when present, the framework uses the provided value as the message identifier instead of generating a new `Guid`. This enables inbox deduplication scenarios where the same logical message is re-published (e.g. broker redelivery simulation):

```csharp
// Original publish — framework generates a new MessageId
await bus.PublishAsync(new PaymentReceived(paymentId, 100m), ct);

// Re-publish with the same MessageId — inbox rejects the duplicate
var headers = new Dictionary<string, string> { ["message-id"] = originalMsgId.ToString() };
await bus.PublishAsync(new PaymentReceived(paymentId, 100m), headers, ct);
```

Framework headers (`BW-MessageType`, trace context) take precedence over custom headers.

> See: `samples/BareWire.Samples.InboxDeduplication/Program.cs`

### Ordered consumption

A custom header can also carry an **ordering key** so a receive endpoint processes messages of the same key in order while running different keys in parallel. Stamp the key on publish (or let the transactional outbox stamp it in `OrderingMode.PerKey`), and opt the endpoint in with `OrderedByHeader("ordering-key")`:

```csharp
var headers = new Dictionary<string, string> { ["ordering-key"] = customerId };
await bus.PublishAsync(new OrderShipped(/* ... */), headers, ct);
```

> See: [Per-Key Consumer Ordering](per-key-ordering.md) for the consumer-side configuration.

## MessageContext.EndpointName

Inside middleware, the `MessageContext.EndpointName` property contains the name of the receive endpoint (queue) processing the current message. This enables endpoint-aware logic such as routing, logging, or inbox deduplication keys:

```csharp
public sealed class AuditMiddleware : IMessageMiddleware
{
    public async Task InvokeAsync(MessageContext context, MessageDelegate next)
    {
        logger.LogInformation("Processing on endpoint {Endpoint}", context.EndpointName);
        await next(context);
    }
}
```

The framework sets `EndpointName` automatically from `EndpointBinding.EndpointName` — no configuration required.

## Routing to a Specific Exchange

By default, every message published via `bus.PublishAsync<T>(...)` lands on the
`DefaultExchange` configured in `UseRabbitMQ`. When different message types need
to land on different exchanges without passing a `BW-Exchange` header on every
call, use `MapExchange<T>(...)` symmetrically to the existing
`MapRoutingKey<T>(...)`:

```csharp
services.AddBareWireRabbitMq(cfg =>
{
    cfg.Host("amqp://guest:guest@localhost:5672/");

    cfg.ConfigureTopology(t =>
    {
        t.DeclareExchange("payments.topic", ExchangeType.Topic);
        t.DeclareExchange("orders.fanout",  ExchangeType.Fanout);
        t.DeclareExchange("default.direct", ExchangeType.Direct);
    });

    cfg.DefaultExchange("default.direct");

    // Type → exchange mapping. The exchange must be declared above; otherwise
    // Build() throws BareWireConfigurationException — manual topology is
    // fail-fast, so an unmapped exchange is rejected at startup.
    cfg.MapExchange<PaymentRequested>("payments.topic");
    cfg.MapExchange<OrderCreated>("orders.fanout");
});
```

### Exchange Resolution Precedence

When `bus.PublishAsync<T>(...)` sends a message, the target exchange is resolved
in the following order — from highest to lowest priority:

| # | Source | When it wins |
|---|---|---|
| a | Explicit `BW-Exchange` header supplied by the caller in `PublishAsync(msg, headers, ct)` | Always, when present — including an empty value (the `queue:` URI scheme relies on an empty header). |
| b | Type → exchange mapping from `MapExchange<T>(...)` | When (a) is absent — BareWire injects `BW-Exchange` into the outbound headers. |
| c | Global `DefaultExchange(...)` | When neither (a) nor (b) applied — the transport adapter falls back to `RabbitMqTransportOptions.DefaultExchange`. |
| d | None of the above | `BareWireConfigurationException` at publish time — configuration is incomplete. |

This lets `bus.PublishAsync(new PaymentRequested(...), ct)` land on
`payments.topic` without any extra code at the call site, while a caller that
*must* force a different exchange (e.g. a MassTransit bridge) can supply
`BW-Exchange` in the headers dictionary on a per-call basis.

## Raw Publishing

Publish raw byte payloads when you need full control over the wire format:

```csharp
await bus.PublishRawAsync(jsonBytes, "application/json", ct);
```
