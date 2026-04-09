# Saga State Machines

BareWire SAGA state machines model long-running business processes as a series of state transitions driven by messages.

## Defining Saga State

Create a state class that implements `ISagaState`:

```csharp
public sealed class OrderSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public Guid OrderId { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public int Version { get; set; }  // optimistic concurrency
}
```

## Defining the State Machine

Extend `BareWireStateMachine<TState, TMessage>` and define states, events, and transitions:

```csharp
public sealed class OrderSagaStateMachine : BareWireStateMachine<OrderSagaState, OrderCreated>
{
    public OrderSagaStateMachine()
    {
        // Define states
        State("Processing");
        State("Shipping");
        State("Compensating");
        State("Completed");
        State("Failed");

        // Initial → Processing
        During("Initial", When<OrderCreated>(context =>
        {
            context.Saga.OrderId = context.Message.OrderId;
            context.Saga.TotalAmount = context.Message.TotalAmount;
            context.Saga.CreatedAt = DateTime.UtcNow;
            context.TransitionTo("Processing");
            context.Schedule<PaymentTimeout>(TimeSpan.FromSeconds(30));
        }));

        // Processing → Shipping (on payment success)
        During("Processing", When<PaymentReceived>(context =>
        {
            context.TransitionTo("Shipping");
        }));

        // Processing → Compensating (on payment failure)
        During("Processing", When<PaymentFailed>(context =>
        {
            context.Saga.FailureReason = context.Message.Reason;
            context.TransitionTo("Compensating");
        }));

        // Shipping → Completed
        During("Shipping", When<ShipmentDispatched>(context =>
        {
            context.Saga.CompletedAt = DateTime.UtcNow;
            context.TransitionTo("Completed");
        }));

        // Compensating → Failed
        During("Compensating", When<CompensationCompleted>(context =>
        {
            context.TransitionTo("Failed");
        }));
    }
}
```

## Persistence

Register SAGA persistence using EF Core:

```csharp
// PostgreSQL
builder.Services.AddBareWireSaga<OrderSagaState>(
    options => options.UseNpgsql(connectionString));

// SQLite (for development)
builder.Services.AddBareWireSaga<OrderSagaState>(
    options => options.UseSqlite("Data Source=saga.db"));
```

Register the state machine and its endpoint:

```csharp
builder.Services.AddSingleton<OrderSagaStateMachine>();

// In transport configuration
rmq.ReceiveEndpoint("order-saga", e =>
{
    e.StateMachineSaga<OrderSagaStateMachine>();
});
```

## Compensable Activities

For complex workflows, define activities that can be compensated on failure:

```csharp
public sealed class ReserveStockActivity : ISagaActivity<OrderSagaState>
{
    public async Task Execute(SagaActivityContext<OrderSagaState> context) { /* reserve */ }
    public async Task Compensate(SagaActivityContext<OrderSagaState> context) { /* release */ }
}

public sealed class ChargePaymentActivity : ISagaActivity<OrderSagaState>
{
    public async Task Execute(SagaActivityContext<OrderSagaState> context) { /* charge */ }
    public async Task Compensate(SagaActivityContext<OrderSagaState> context) { /* refund */ }
}
```

> See: `samples/BareWire.Samples.SagaOrderFlow/Activities/`

## Querying Saga State

Inject `ISagaRepository<TState>` to query current saga instances:

```csharp
app.MapGet("/orders/{id}/status", async (
    Guid id,
    ISagaRepository<OrderSagaState> repository,
    CancellationToken ct) =>
{
    var saga = await repository.FindAsync(id, ct);
    return saga is null
        ? Results.NotFound()
        : Results.Ok(new { saga.CurrentState, saga.OrderId, saga.CreatedAt });
});
```

## Scheduled Timeouts

Schedule a timeout event that fires if no message arrives within a given period:

```csharp
context.Schedule<PaymentTimeout>(TimeSpan.FromSeconds(30));
```

If `PaymentReceived` arrives before the timeout, the saga transitions normally. Otherwise, `PaymentTimeout` triggers the compensating flow.

> See: `samples/BareWire.Samples.SagaOrderFlow/`
