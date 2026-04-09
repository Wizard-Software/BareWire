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

## Raw Publishing

Publish raw byte payloads when you need full control over the wire format:

```csharp
await bus.PublishRawAsync(jsonBytes, "application/json", ct);
```
