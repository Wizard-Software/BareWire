# MassTransit v8.x Public API Reference

**Source:** MassTransit GitHub repository (v8.5.10)  
**Documentation:** https://masstransit.massient.com/documentation  
**Repository:** https://github.com/MassTransit/MassTransit

---

## 1. Configuration API

### 1.1 Dependency Injection Registration

**Method Signature:**
```csharp
public static IServiceCollection AddMassTransit(this IServiceCollection collection, 
    Action<IBusRegistrationConfigurator> configure = null)
```

**Source:** `/src/MassTransit/Configuration/DependencyInjection/DependencyInjectionRegistrationExtensions.cs`

**Key Details:**
- Extension method on `IServiceCollection` (Microsoft.Extensions.DependencyInjection)
- Returns `IServiceCollection` for method chaining
- Optional `configure` parameter accepts `IBusRegistrationConfigurator` delegate
- Throws `ConfigurationException` if called more than once per container
- Auto-registers MassTransit components, hosted service, instrumentation, usage tracking

**Alternative Overload (Multi-Bus):**
```csharp
public static IServiceCollection AddMassTransit<TBus, TBusInstance>(this IServiceCollection collection,
    Action<IBusRegistrationConfigurator<TBus>> configure)
    where TBus : class, IBus
    where TBusInstance : BusInstance<TBus>, TBus
```

### 1.2 Transport Configuration - RabbitMQ

**Method Signature:**
```csharp
public static void UsingRabbitMq(this IBusRegistrationConfigurator configurator,
    Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator> configure = null)
```

**Source:** `/src/Transports/MassTransit.RabbitMqTransport/Configuration/RabbitMqBusFactoryConfiguratorExtensions.cs`

**Key Details:**
- Called within `AddMassTransit()` configuration delegate
- Parameters: `IBusRegistrationContext` (DI context), `IRabbitMqBusFactoryConfigurator` (RabbitMQ-specific settings)
- Sets the bus factory to `RabbitMqRegistrationBusFactory`
- Configure parameter is optional

**Usage Pattern:**
```csharp
services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("rabbitmq://localhost");
        cfg.ConfigureEndpoints(context);
    });
});
```

### 1.3 Endpoint Configuration

**Method Signature:**
```csharp
public static void ConfigureEndpoints<T>(this IBusFactoryConfigurator<T> configurator, 
    IBusRegistrationContext registration,
    IEndpointNameFormatter formatter = null)
```

**Source:** `/src/MassTransit/Configuration/RegistrationContextExtensions.cs`

**Key Details:**
- Automatically configures all registered consumers and sagas as receive endpoints
- Called on the bus factory configurator (e.g., `IRabbitMqBusFactoryConfigurator`)
- Accepts optional `IEndpointNameFormatter` for custom queue/exchange naming
- Discovers all `IConsumer<T>`, `SagaStateMachine<T>`, and `IJobConsumer` implementations
- Respects `ConfigureConsumeTopology` attribute on message types

**Receive Endpoint (Manual):**
```csharp
public static void ReceiveEndpoint(this ISqlBusFactoryConfigurator configurator, 
    Action<ISqlReceiveEndpointConfigurator> configure = null)
```

**Key Details:**
- Creates individual receive endpoints with custom configuration
- Accessed via `IRabbitMqBusFactoryConfigurator`, `IServiceBusFactoryConfigurator`, etc.
- Per-transport implementation (SQL, RabbitMQ, Azure Service Bus, etc.)
- Configure consumers via `cfg.Consumer<T>(registration)`

---

## 2. Consumer Interface

### 2.1 IConsumer<T> Interface

**Interface Definition:**
```csharp
public interface IConsumer<in TMessage> :
    IConsumer
    where TMessage : class
{
    Task Consume(ConsumeContext<TMessage> context);
}

public interface IConsumer
{
    // Marker interface for IoC container identification
}
```

**Source:** `/src/MassTransit.Abstractions/IConsumer.cs`

**Key Details:**
- Generic interface parameterized by message type (`TMessage : class`)
- Single method: `Consume()` (not `ConsumeAsync()`)
- Return type: `Task` (async-only, no `void`)
- Parameter: `ConsumeContext<TMessage>` contains message + metadata
- Non-generic `IConsumer` is a marker interface for reflection-based container discovery

**Implementation Example:**
```csharp
public class OrderConsumer : IConsumer<OrderCreated>
{
    public async Task Consume(ConsumeContext<OrderCreated> context)
    {
        // Process message
        await context.Publish<OrderProcessed>(new { context.Message.OrderId });
    }
}
```

---

## 3. Message Context & Headers

### 3.1 ConsumeContext<T> Interface

**Base Interfaces:**
```csharp
public interface ConsumeContext<out T> :
    ConsumeContext
    where T : class
{
    T Message { get; }
}

public interface ConsumeContext :
    PipeContext,
    MessageContext,
    IPublishEndpoint,
    ISendEndpointProvider
{
    // Additional members (see MessageContext properties below)
}
```

**Source:** `/src/MassTransit.Abstractions/Contexts/ConsumeContext.cs`

### 3.2 MessageContext Properties

**Interface Definition:**
```csharp
public interface MessageContext
{
    Guid? MessageId { get; }
    Guid? RequestId { get; }
    Guid? CorrelationId { get; }
    Guid? ConversationId { get; }
    Guid? InitiatorId { get; }
    DateTime? ExpirationTime { get; }
    Uri? SourceAddress { get; }
    Uri? DestinationAddress { get; }
    Uri? ResponseAddress { get; }
    Uri? FaultAddress { get; }
    DateTime? SentTime { get; }
    Headers Headers { get; }
    HostInfo Host { get; }
}
```

**Source:** `/src/MassTransit.Abstractions/Contexts/MessageContext.cs`

**Property Descriptions:**

| Property | Type | Purpose |
|----------|------|---------|
| `MessageId` | `Guid?` | Unique identifier assigned at send time (not transport MessageId) |
| `RequestId` | `Guid?` | Correlates responses/faults to original request |
| `CorrelationId` | `Guid?` | User-assigned operation identifier (spans related messages) |
| `ConversationId` | `Guid?` | Groups related messages together in a conversation flow |
| `InitiatorId` | `Guid?` | CorrelationId from the consumed message (saga origin) |
| `ExpirationTime` | `DateTime?` | Message expiration deadline (optional TTL) |
| `SourceAddress` | `Uri?` | Producer endpoint address |
| `DestinationAddress` | `Uri?` | Consumer endpoint address |
| `ResponseAddress` | `Uri?` | Reply-to address for request/response patterns |
| `FaultAddress` | `Uri?` | Fault handling address for failures |
| `SentTime` | `DateTime?` | UTC timestamp of original send |
| `Headers` | `Headers` | Application-specific metadata (Dictionary-like) |
| `Host` | `HostInfo` | Producer machine info (name, version, CorrelationId, etc.) |

### 3.3 Additional ConsumeContext Methods

**Key Methods:**
```csharp
// Publishing from consumer
Task Publish<T>(T message)
Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe)

// Responding to request
Task RespondAsync<T>(T message)
Task RespondAsync<T>(object values) // Dynamic instantiation

// Checking/converting message types
bool HasMessageType(Type messageType)
bool TryGetMessage<T>(out ConsumeContext<T> consumeContext)

// Task tracking
void AddConsumeTask(Task task)
Task ConsumeCompleted { get; }  // Completes when consumer done

// Notifications (internal)
Task NotifyConsumed(TimeSpan duration, string consumerType)
Task NotifyFaulted(TimeSpan duration, string consumerType, Exception exception)
```

---

## 4. Publishing & Sending

### 4.1 IPublishEndpoint Interface

**Method Signatures:**
```csharp
public interface IPublishEndpoint
{
    // Strongly-typed publish
    Task Publish<T>(T message, CancellationToken cancellationToken = default)
        where T : class;
    
    Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, 
        CancellationToken cancellationToken = default)
        where T : class;
    
    Task Publish<T>(T message, IPipe<PublishContext> publishPipe, 
        CancellationToken cancellationToken = default)
        where T : class;

    // Dynamic/untyped publish
    Task Publish(object message, CancellationToken cancellationToken = default);
    
    Task Publish(object message, IPipe<PublishContext> publishPipe, 
        CancellationToken cancellationToken = default);
    
    Task Publish(object message, Type messageType, CancellationToken cancellationToken = default);
    
    Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, 
        CancellationToken cancellationToken = default);

    // Interface-based with dictionary initialization
    Task Publish<T>(object values, CancellationToken cancellationToken = default)
        where T : class;
    
    Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, 
        CancellationToken cancellationToken = default)
        where T : class;
    
    Task Publish<T>(object values, IPipe<PublishContext> publishPipe, 
        CancellationToken cancellationToken = default)
        where T : class;
}
```

**Source:** `/src/MassTransit.Abstractions/IPublishEndpoint.cs`

**Key Details:**
- Fan-out to all subscribed consumers (publish-subscribe pattern)
- Always returns `Task` (async-only)
- Supports `CancellationToken` (defaults to `default`)
- `IPipe<PublishContext<T>>` allows header/metadata customization
- Dynamic publish accepts `Type` parameter or `object values` for interface hydration

### 4.2 ISendEndpoint Interface

**Method Signatures:**
```csharp
public interface ISendEndpoint
{
    // Strongly-typed send
    Task Send<T>(T message, CancellationToken cancellationToken = default)
        where T : class;
    
    Task Send<T>(T message, IPipe<SendContext<T>> pipe, 
        CancellationToken cancellationToken = default)
        where T : class;
    
    Task Send<T>(T message, IPipe<SendContext> pipe, 
        CancellationToken cancellationToken = default)
        where T : class;

    // Untyped send
    Task Send(object message, CancellationToken cancellationToken = default);
    
    Task Send(object message, Type messageType, CancellationToken cancellationToken = default);
    
    Task Send(object message, IPipe<SendContext> pipe, 
        CancellationToken cancellationToken = default);
    
    Task Send(object message, Type messageType, IPipe<SendContext> pipe, 
        CancellationToken cancellationToken = default);

    // Interface-based with dictionary initialization
    Task Send<T>(object values, CancellationToken cancellationToken = default)
        where T : class;
    
    Task Send<T>(object values, IPipe<SendContext<T>> pipe, 
        CancellationToken cancellationToken = default)
        where T : class;
    
    Task Send<T>(object values, IPipe<SendContext> pipe, 
        CancellationToken cancellationToken = default)
        where T : class;
}
```

**Source:** `/src/MassTransit.Abstractions/ISendEndpoint.cs`

**Key Differences from Publish:**
- Point-to-point routing (single consumer)
- Returns `Task` acknowledging broker acceptance
- Requires explicit recipient address (via pipe configuration)
- `Send()` not `SendAsync()`

### 4.3 IBus Interface

**Declaration:**
```csharp
public interface IBus :
    IPublishEndpoint,
    IPublishEndpointProvider,
    ISendEndpointProvider,
    IConsumePipeConnector,
    IRequestPipeConnector,
    IConsumeMessageObserverConnector,
    IConsumeObserverConnector,
    IReceiveObserverConnector,
    IReceiveEndpointObserverConnector,
    IReceiveConnector,
    IProbeSite
{
    Uri Address { get; }
    IBusTopology Topology { get; }
}
```

**Source:** `/src/MassTransit.Abstractions/IBus.cs`

**Key Details:**
- Inherits from `IPublishEndpoint` and `ISendEndpointProvider`
- Provides bus-wide `Address` and `Topology` properties
- `Address` = InputAddress of default bus endpoint
- `Topology` = `IBusTopology` for introspection of published/consumed message types

---

## 5. Request-Response Pattern

### 5.1 IRequestClient<TRequest> Interface

**Method Signatures:**
```csharp
public interface IRequestClient<TRequest>
    where TRequest : class
{
    // Create a request handle
    RequestHandle<TRequest> Create(TRequest message, CancellationToken cancellationToken = default, 
        RequestTimeout timeout = default);
    
    RequestHandle<TRequest> Create(object values, CancellationToken cancellationToken = default, 
        RequestTimeout timeout = default);

    // Get single response
    Task<Response<T>> GetResponse<T>(TRequest message, CancellationToken cancellationToken = default, 
        RequestTimeout timeout = default)
        where T : class;
    
    Task<Response<T>> GetResponse<T>(TRequest message, RequestPipeConfiguratorCallback<TRequest> callback, 
        CancellationToken cancellationToken = default, RequestTimeout timeout = default)
        where T : class;
    
    Task<Response<T>> GetResponse<T>(object values, CancellationToken cancellationToken = default, 
        RequestTimeout timeout = default)
        where T : class;
    
    Task<Response<T>> GetResponse<T>(object values, RequestPipeConfiguratorCallback<TRequest> callback, 
        CancellationToken cancellationToken = default, RequestTimeout timeout = default)
        where T : class;

    // Get multiple response types (2-3 variants each)
    Task<Response<T1, T2>> GetResponse<T1, T2>(TRequest message, CancellationToken cancellationToken = default,
        RequestTimeout timeout = default)
        where T1 : class
        where T2 : class;
    
    Task<Response<T1, T2, T3>> GetResponse<T1, T2, T3>(TRequest message, CancellationToken cancellationToken = default,
        RequestTimeout timeout = default)
        where T1 : class
        where T2 : class
        where T3 : class;
    
    // ... and callback variants for each
}
```

**Source:** `/src/MassTransit.Abstractions/Clients/IRequestClient.cs`

**Key Details:**
- Injected into consumers/services (via DI registration)
- `Create()` returns `RequestHandle<TRequest>` for further configuration
- `GetResponse<T>()` is the main pattern for request-response
- Supports 1, 2, or 3 response types in tuple form
- `RequestTimeout` struct for optional timeout (default = infinite)
- `RequestPipeConfiguratorCallback<TRequest>` allows request pipe customization
- Automatic `RequestId` correlation + reply-to address setup

**Usage Pattern:**
```csharp
var client = serviceProvider.GetRequiredService<IRequestClient<GetOrderRequest>>();
var response = await client.GetResponse<OrderResponse>(new GetOrderRequest { OrderId = 123 });
// response.Message contains the OrderResponse
```

---

## 6. Saga State Machines

### 6.1 SagaStateMachine<TSaga> Interface

**Interface Declaration:**
```csharp
public interface SagaStateMachine<TSaga> :
    StateMachine<TSaga>
    where TSaga : class, SagaStateMachineInstance
{
    IEnumerable<EventCorrelation> Correlations { get; }
    
    Task<bool> IsCompleted(BehaviorContext<TSaga> context);
}
```

**Source:** `/src/MassTransit.Abstractions/SagaStateMachine/SagaStateMachine.cs`

### 6.2 SagaStateMachineInstance Interface

**Interface Declaration:**
```csharp
public interface SagaStateMachineInstance :
    ISaga
{
    // Empty interface - just a marker
}
```

**Source:** `/src/MassTransit.Abstractions/SagaStateMachine/SagaStateMachineInstance.cs`

**Implementation Requirements:**
```csharp
public class OrderState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }  // Saga identity
    public string CurrentState { get; set; }  // State name
    
    // User-defined saga data fields
}
```

### 6.3 State Machine DSL

**[UNCERTAIN]** Exact DSL method signatures for `Initially`, `During`, `Event()`, `State()`, `CorrelateBy()` require checking Automatonymous base class (not in MassTransit.Abstractions). MassTransit inherits state machine DSL from Automatonymous library.

**Known Pattern (from documentation):**
```csharp
public class OrderStateMachine : MassTransitStateMachine<OrderState>
{
    // Define states
    public State Initial { get; private set; }
    public State Submitted { get; private set; }
    public State Completed { get; private set; }

    // Define events
    public Event<SubmitOrder> Submit { get; private set; }
    public Event<OrderCompleted> Complete { get; private set; }

    public OrderStateMachine()
    {
        // Configure initial state
        Initially(
            When(Submit, context => HandleSubmit(context))
                .TransitionTo(Submitted)
        );

        // Configure transitions
        During(Submitted,
            When(Complete, context => HandleComplete(context))
                .Finalize()
        );
    }
}
```

**Key Details:**
- Base class: `MassTransitStateMachine<TSaga>` (defined in MassTransit, not Abstractions)
- DSL keywords: `Initially()`, `During()`, `Event()`, `State()`, `CorrelateBy()`
- Transitions via `TransitionTo()`, state completion via `Finalize()`
- Event correlation via `CorrelateBy(message => message.OrderId)`
- Configuration required for `EventCorrelation.ConfigureConsumeTopology` (bool)

**Event Correlation Attribute:**
```csharp
public interface EventCorrelation
{
    Type DataType { get; }
    bool ConfigureConsumeTopology { get; }  // Default: true
}
```

---

## 7. Serialization & Envelope

### 7.1 Default Format

**Message Envelope Structure:**
- **Content-Type:** `application/vnd.masstransit+json`
- **Envelope serializer:** System.Text.Json (default) or Newtonsoft.Json
- **Envelope fields:** (verified by BareWire B2 integration test — live MT 8.5.10 on RabbitMQ)
  - `messageId`: Guid string — MT assigns a new one per receive; does NOT echo the sender's value
  - `requestId`: Guid string — MT parses from envelope and exposes as `ConsumeContext.RequestId`; used for response correlation
  - `correlationId`: Guid string — MT parses from envelope and exposes as `ConsumeContext.CorrelationId`
  - `responseAddress`: URI string — MT parses and exposes as `ConsumeContext.ResponseAddress`; used by `RespondAsync()` to route the reply
  - `destinationAddress`: URI string — destination endpoint address
  - `faultAddress`: URI string — fault endpoint address
  - `expirationTime`: ISO8601 string — maps to `MessageContext.ExpirationTime`; also sets AMQP `Expiration` header
  - `sentTime`: ISO8601 string (from `MessageContext.SentTime`)
  - `messageType`: Array of URNs — format `urn:message:{Namespace}:{TypeName}` (NOT fully-qualified); MT matches by short type name
  - `headers`: object — custom application headers
  - `message`: object — actual message payload

**Source:** `/src/MassTransit.Newtonsoft/Serialization/` and `/src/Transports/MassTransit.RabbitMqTransport/`

**Verified JSON envelope example (BareWire PRODUCTION → MT, B2 test 2026-06-20):**

`responseAddress` is produced by `RabbitMqEndpointAddress.BuildReplyToAddress` (called in
`RabbitMqRequestClient.InitializeAsync`). The AMQP `ReplyTo` property (not shown here) carries
the actual exclusive reply-queue name (`amq.gen-xxx`).

```json
{
  "messageId": "<uuid>",
  "requestId": "<uuid>",
  "responseAddress": "rabbitmq://host:port/amq.rabbitmq.reply-to",
  "expirationTime": "2026-06-20T21:47:05.000Z",
  "messageType": ["urn:message:BareWire.IntegrationTests.Interop:MtInteropPingRequest"],
  "sentTime": "2026-06-20T21:47:00.000Z",
  "message": { "payload": "hello-from-barewire" }
}
```

**D2 Wire-Level Observations (verified by live production-path test, MT 8.5.10):**

| Observation | Verified Fact |
|-------------|---------------|
| `requestId` parsed by MT | `ConsumeContext.RequestId` is non-null when BareWire sets `requestId` in envelope |
| `responseAddress` routing | MT reads `responseAddress` from envelope, NOT from AMQP `reply-to` header, to route `RespondAsync()` |
| Reply-to mechanism | When `responseAddress` ends with `amq.rabbitmq.reply-to`, MT uses AMQP `ReplyTo` property (actual queue name) as routing key to default exchange — response reaches BareWire's exclusive reply queue |
| Fanout exchange trap | If `responseAddress` = `rabbitmq://host/amq.gen-xxx?temporary=true`, MT declares a fanout exchange `amq.gen-xxx` and publishes to it — silently dropped (no binding) |
| Correlation path | MT's `RespondAsync()` echoes `requestId` as AMQP `correlation_id` on the response message; BareWire's Stage-1 correlation (AMQP CorrelationId) fires |
| `messageType` URN format | `urn:message:{Namespace}:{TypeName}` — NOT `{FullyQualifiedTypeName}`; namespace + simple name only |

### 7.2 Serializer Configuration

**Via Configuration Pipe:**
```csharp
cfg.ReceiveEndpoint("queue-name", endpoint =>
{
    endpoint.UseSerializer<SystemJsonMessageSerializer>();  // or
    endpoint.UseSerializer<NewtonsoftJsonMessageSerializer>();
});
```

---

## 8. Topology Configuration

### 8.1 ConfigureConsumeTopology Default

**Attribute Definition:**
```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public class ConfigureConsumeTopologyAttribute : Attribute
{
    public ConfigureConsumeTopologyAttribute()
    {
        ConfigureConsumeTopology = true;  // Default: auto-create topology
    }

    public ConfigureConsumeTopologyAttribute(bool configureConsumeTopology)
    {
        ConfigureConsumeTopology = configureConsumeTopology;
    }

    public bool ConfigureConsumeTopology { get; }
}
```

**Source:** `/src/MassTransit.Abstractions/Attributes/ConfigureConsumeTopologyAttribute.cs`

**IReceiveEndpointConfigurator Property:**
```csharp
public interface IReceiveEndpointConfigurator
{
    bool ConfigureConsumeTopology { set; }  // Per-endpoint override
}
```

**ConnectPipeOptions Enum:**
```csharp
public enum ConnectPipeOptions
{
    ConfigureConsumeTopology = 1,
    All = ConfigureConsumeTopology
}
```

**Key Details:**
- **Default:** `true` — auto-create exchanges/topics and bind queues
- **Per-message override:** `[ConfigureConsumeTopology(false)]` on message type
- **Per-endpoint override:** `endpoint.ConfigureConsumeTopology = false`
- When `false`: No broker topology created for that message type on that endpoint

### 8.2 Topology Naming

**Naming Customization:**
```csharp
// Global formatter (custom IEntityNameFormatter)
// Message-specific formatter (custom IMessageEntityNameFormatter<T>)
// Attribute on message type: [EntityName("custom-name")]
// Configuration: SetEntityName("custom-name")
```

**Auto-Topology Behavior:**
- Generated from message type names via `IEntityNameFormatter`
- Auto-applies `CorrelationId` detection
- Creates bindings based on consumer registrations
- `DeployPublishTopology` flag deploys all topologies on bus start

---

## 9. Middleware & Filters

### 9.1 IFilter<TContext> Interface

**Interface Definition:**
```csharp
public interface IFilter<TContext> :
    IProbeSite
    where TContext : class, PipeContext
{
    Task Send(TContext context, IPipe<TContext> next);
}
```

**Source:** `/src/MassTransit.Abstractions/Middleware/IFilter.cs`

**Key Details:**
- Generic filter for any pipe context type
- Single method: `Send()` returns `Task`
- `next` parameter chains to next filter in pipeline
- Implementations are singletons (stateless)

### 9.2 Filter Usage Patterns

**[UNCERTAIN]** Exact extension methods for `UseFilter()`, `UseRetry()`, etc. are defined in concrete transport/configuration classes, not in Abstractions. Common patterns include:

```csharp
cfg.UseFilter<LoggingFilter<ConsumeContext>>();  // Per-transport method
cfg.UseRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
```

---

## 10. Known Breaking Changes & Differences from Earlier Versions

### 10.1 MassTransit v8.x vs Earlier

**Message Format:**
- Envelope includes `messageType` array (for polymorphism support)
- Headers stored separately from message body
- `CorrelationId` is optional (not required on all messages)

**Consumer Interface:**
- Method name is `Consume()` (not `ConsumeAsync()`)
- Return type is `Task` (not `void`)

**Configuration:**
- `AddMassTransit()` registration is required (no static methods)
- `ConfigureConsumeTopology` defaults to `true` (auto-topology enabled)
- Fluent API via `IBusRegistrationConfigurator`

**Routing:**
- `Request/Response` uses `RequestId` + `ResponseAddress` (explicit reply-to)
- No implicit dead-letter routing (must configure fault endpoints)

---

## 11. Dependency Injection Registration Checklist

### Consumer Registration
```csharp
services.AddMassTransit(x =>
{
    x.AddConsumer<OrderConsumer>();  // Auto-discovery via reflection
    x.AddConsumersFromNamespaceContaining<OrderConsumer>();  // Batch registration
    
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.ConfigureEndpoints(context);  // Auto-creates receive endpoints
    });
});
```

### Saga Registration
```csharp
x.AddSagaStateMachine<OrderStateMachine, OrderState>()
    .InMemoryRepository();  // or .EntityFrameworkRepository() etc.
```

### Request Client Registration
```csharp
x.AddRequestClient<GetOrderRequest>();  // Auto-registers IRequestClient<T>
```

---

## 12. Summary of Exact Type Names

| Entity | Type | Namespace |
|--------|------|-----------|
| Bus registration | `IBus` | `MassTransit` |
| Publish endpoint | `IPublishEndpoint` | `MassTransit` |
| Send endpoint | `ISendEndpoint` | `MassTransit` |
| Consume context (typed) | `ConsumeContext<T>` | `MassTransit` |
| Consume context (base) | `ConsumeContext` | `MassTransit` |
| Message context | `MessageContext` | `MassTransit` |
| Consumer interface | `IConsumer<T>` | `MassTransit` |
| Request client | `IRequestClient<T>` | `MassTransit` |
| Response | `Response<T>` | `MassTransit` |
| Saga base | `SagaStateMachine<T>` | `MassTransit` |
| Saga instance | `SagaStateMachineInstance` | `MassTransit` |
| Filter | `IFilter<TContext>` | `MassTransit` |
| Pipe | `IPipe<TContext>` | `MassTransit` |
| Bus registration config | `IBusRegistrationConfigurator` | `MassTransit` |
| RabbitMQ bus config | `IRabbitMqBusFactoryConfigurator` | `MassTransit.RabbitMqTransport` |
| Receive endpoint config | `IReceiveEndpointConfigurator` | `MassTransit` |
| DI extensions | `DependencyInjectionRegistrationExtensions` | `MassTransit` |

---

## References

- **GitHub Repository:** https://github.com/MassTransit/MassTransit (v8.5.10)
- **Documentation:** https://masstransit.massient.com/documentation
- **Source Paths:** All paths relative to `/src/` directory
