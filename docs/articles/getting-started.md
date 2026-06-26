# Getting Started

## Installation

Add the BareWire packages to your project:

```bash
dotnet add package BareWire.Abstractions
dotnet add package BareWire
dotnet add package BareWire.Serialization.Json
dotnet add package BareWire.Transport.RabbitMQ
```

## Define a Message

Messages are plain C# records — no base class or attributes required. Use past tense for events and imperative for commands:

```csharp
// Event (something that happened)
public record MessageSent(Guid Id, string Content, DateTime SentAt);

// Command (something you want to happen)
public record ProcessPayment(Guid OrderId, decimal Amount);
```

## Create a Consumer

Implement `IConsumer<T>` to handle messages. Consumers are `sealed` by convention and receive dependencies via constructor injection:

```csharp
public sealed class MessageConsumer : IConsumer<MessageSent>
{
    private readonly SampleDbContext _db;
    private readonly ILogger<MessageConsumer> _logger;

    public MessageConsumer(SampleDbContext db, ILogger<MessageConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ConsumeAsync(ConsumeContext<MessageSent> context)
    {
        _logger.LogInformation("Received: {Content}", context.Message.Content);

        _db.ReceivedMessages.Add(new ReceivedMessage
        {
            Id = context.Message.Id,
            Content = context.Message.Content,
            ReceivedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(context.CancellationToken);
    }
}
```

> See: `samples/BareWire.Samples.BasicPublishConsume/Consumers/MessageConsumer.cs`

## Configure the Bus

Register BareWire in your DI container with the RabbitMQ transport. The `BareWire.RabbitMQ`
bundle package registers the core and the RabbitMQ transport in a single call. Topology is
manual by default — you declare exchanges, queues, and bindings explicitly:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register JSON serializer (raw-first, no envelope)
builder.AddBareWireJsonSerializer();

// Configure BareWire with RabbitMQ in one call
builder.Services.AddBareWireWithRabbitMq(transport =>
{
    transport.Host("amqp://guest:guest@localhost:5672/");

    // Declare topology explicitly
    transport.ConfigureTopology(topology =>
    {
        topology.DeclareExchange("messages", ExchangeType.Fanout, durable: true);
        topology.DeclareQueue("messages", durable: true);
        topology.BindExchangeToQueue("messages", "messages");
    });

    // Register consumer on an endpoint
    transport.ReceiveEndpoint("messages", e =>
    {
        e.PrefetchCount = 16;
        e.Consumer<MessageConsumer, MessageSent>();
    });
});
```

`AddBareWireWithRabbitMq` takes an optional second `bus` delegate for core configuration
(consumers registered on the bus, middleware, serializers). For an application that needs more
than one transport — or finer control over package references — register the core and transport
separately instead; see [Configuration](configuration.md#bus-registration) for all three
registration paths and why the engine and transport are layered the way they are.

> See: `samples/BareWire.Samples.BasicPublishConsume/Program.cs`

## Publish a Message

Inject `IPublishEndpoint` (or `IBus`) and call `PublishAsync`:

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

## Run with Aspire

The simplest way to run any sample is via the Aspire AppHost, which provisions RabbitMQ and PostgreSQL automatically:

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

> See: `samples/BareWire.Samples.AppHost/Program.cs`

## Next Steps

- [Configuration](configuration.md) — detailed bus and transport options
- [Publishing and Consuming](publishing-and-consuming.md) — patterns for pub/sub, request-response, and raw messages
- [Topology](topology.md) — exchange types, routing keys, and binding strategies
