# Saga State Machines

BareWire saga state machines model long-running business processes as a series of state transitions
driven by messages. A state machine is a class deriving from `BareWireStateMachine<TSaga>`, where
`TSaga` is the persistent saga **state** type. Correlation, transitions, side-effects, and timeouts
are declared with a fluent DSL in the constructor.

## Defining Saga State

Create a state class that implements `ISagaState`. The interface requires three members —
`CorrelationId`, `CurrentState` (start it at `"Initial"`), and `Version` (optimistic concurrency) —
plus any business properties you need. Properties are mutable because the persistence layer
materialises rows into existing instances.

```csharp
public sealed class OrderSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }                 // optimistic concurrency

    // Business properties
    public string? OrderId { get; set; }
    public decimal Amount { get; set; }
    public string? ShippingAddress { get; set; }
    public string? PaymentId { get; set; }
    public string? TrackingNumber { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

> See: `samples/BareWire.Samples.SagaOrderFlow/Saga/OrderSagaState.cs`

## Defining the State Machine

Derive from `BareWireStateMachine<TSaga>` (a **single** type parameter — the saga state) and define
events, states, scheduled timeouts, correlation, and transitions in the constructor:

- `Event<T>()` declares a typed event and returns a handle.
- `State("Name")` declares a named state and returns a handle (the `"Initial"` state is implicit).
- `Schedule<T>(cfg => ...)` declares a scheduled timeout and returns a handle.
- `CorrelateBy<T>(e => guid)` maps each event message to a saga `CorrelationId`.
- `Initially(...)`, `During(state, ...)`, `DuringAny(...)`, and `Finally(...)` open a scope, inside
  which `When(eventHandle, b => ...)` wires an event to an activity chain.

```csharp
public sealed class OrderSagaStateMachine : BareWireStateMachine<OrderSagaState>
{
    public OrderSagaStateMachine()
    {
        // Events
        var orderCreated = Event<OrderCreated>();
        var paymentReceived = Event<PaymentReceived>();
        var paymentTimeout = Event<PaymentTimeout>();
        var shipmentDispatched = Event<ShipmentDispatched>();

        // States ("Initial" is implicit)
        var processing = State("Processing");
        var shipping = State("Shipping");
        var compensating = State("Compensating");
        var completed = State("Completed");

        // Scheduled 30s payment timeout
        var paymentTimeoutSchedule = Schedule<PaymentTimeout>(cfg =>
        {
            cfg.Delay = TimeSpan.FromSeconds(30);
            cfg.Strategy = SchedulingStrategy.Auto;
        });

        // Correlation — map every event to a saga by OrderId
        CorrelateBy<OrderCreated>(e => Guid.Parse(e.OrderId));
        CorrelateBy<PaymentReceived>(e => Guid.Parse(e.OrderId));
        CorrelateBy<PaymentTimeout>(e => Guid.Parse(e.OrderId));
        CorrelateBy<ShipmentDispatched>(e => Guid.Parse(e.OrderId));

        // Initial → OrderCreated → Processing (schedule the timeout)
        Initially(() =>
        {
            When(orderCreated, b => b
                .Then((saga, evt) =>
                {
                    saga.OrderId = evt.OrderId;
                    saga.Amount = evt.Amount;
                    saga.CreatedAt = DateTimeOffset.UtcNow;
                    return Task.CompletedTask;
                })
                .ScheduleTimeout<PaymentTimeout>((saga, evt) => new PaymentTimeout(evt.OrderId), paymentTimeoutSchedule)
                .TransitionTo(processing.Name));
        });

        During(processing, () =>
        {
            // PaymentReceived → Shipping (cancel the timeout)
            When(paymentReceived, b => b
                .Then((saga, evt) => { saga.PaymentId = evt.PaymentId; return Task.CompletedTask; })
                .CancelTimeout<PaymentTimeout>()
                .TransitionTo(shipping.Name));

            // PaymentTimeout → Compensating
            When(paymentTimeout, b => b
                .Then((saga, evt) => { saga.FailureReason = "Payment timeout."; return Task.CompletedTask; })
                .TransitionTo(compensating.Name));
        });

        During(shipping, () =>
        {
            // ShipmentDispatched → Completed (finalize — deletes the saga row)
            When(shipmentDispatched, b => b
                .Then((saga, evt) => { saga.TrackingNumber = evt.TrackingNumber; return Task.CompletedTask; })
                .TransitionTo(completed.Name)
                .Finalize());
        });
    }
}
```

> See: `samples/BareWire.Samples.SagaOrderFlow/Saga/OrderSagaStateMachine.cs`

### Activity-chain methods (`IEventActivityBuilder<TSaga, TEvent>`)

Inside `When(handle, b => ...)`, chain the activities the saga runs for that event. Every method
returns the builder, so calls compose fluently:

| Method | Effect |
|--------|--------|
| `Then(Func<TSaga, TEvent, Task>)` | Runs an async action with access to the saga state and the event. |
| `TransitionTo(string state)` | Moves the saga to the named state after the chain runs. |
| `Publish<T>(Func<TSaga, TEvent, T>)` | Publishes a message built from the saga and event. |
| `Send<T>(Uri, Func<TSaga, TEvent, T>)` | Sends a message to a specific endpoint. |
| `ScheduleTimeout<T>(Func<TSaga, TEvent, T>, ScheduleHandle<T>)` | Schedules a timeout message using a handle from `Schedule<T>(...)`. |
| `CancelTimeout<T>()` | Cancels a previously scheduled timeout of that type. |
| `Finalize()` | Marks the saga finalized — the repository deletes it after the chain completes. |

## Scheduled Timeouts

Declare a schedule once with `Schedule<T>(cfg => ...)`, then use the returned handle in the activity
chain with `ScheduleTimeout<T>(factory, handle)` and cancel it with `CancelTimeout<T>()`:

```csharp
var paymentTimeoutSchedule = Schedule<PaymentTimeout>(cfg =>
{
    cfg.Delay = TimeSpan.FromSeconds(30);
    cfg.Strategy = SchedulingStrategy.Auto;   // Auto | TransportNative | DelayRequeue | ExternalScheduler | DelayTopic
});

// in a transition:
When(orderCreated, b => b.ScheduleTimeout<PaymentTimeout>((saga, evt) => new PaymentTimeout(evt.OrderId), paymentTimeoutSchedule));
// cancel it when payment arrives:
When(paymentReceived, b => b.CancelTimeout<PaymentTimeout>());
```

`SchedulingStrategy.Auto` lets BareWire pick the delivery mechanism from the transport's
capabilities. The other values (`TransportNative`, `DelayRequeue`, `ExternalScheduler`, `DelayTopic`)
select a specific strategy.

## Registration

Two registrations are required, plus the endpoint that hosts the machine:

```csharp
// 1. Persistence — registers ISagaRepository<OrderSagaState> (EF Core, BareWire.Saga.EntityFramework).
builder.Services.AddBareWireSaga<OrderSagaState>(
    options => options.UseNpgsql(connectionString),
    autoCreateSchema: true);          // dev convenience; manage schema via migrations in production

// SQLite for local development:
// builder.Services.AddBareWireSaga<OrderSagaState>(options => options.UseSqlite("Data Source=saga.db"));

// 2. State machine — registers the machine and wires its dispatcher into the consume pipeline (BareWire.Saga).
builder.Services.AddBareWireSagaStateMachine<OrderSagaStateMachine, OrderSagaState>();

// 3. Host the machine on a receive endpoint (pass the state-machine type).
rmq.ReceiveEndpoint("order-saga", e =>
{
    e.RetryCount = 3;
    e.RetryInterval = TimeSpan.FromSeconds(2);
    e.StateMachineSaga<OrderSagaStateMachine>();
});
```

`AddBareWireSaga<TSaga>` (the EF Core repository) lives in `BareWire.Saga.EntityFramework`;
`AddBareWireSagaStateMachine<TStateMachine, TSaga>` lives in `BareWire.Saga`. For a Redis-backed
repository instead of EF Core, see [Redis Saga Persistence](saga-redis.md).

> See: `samples/BareWire.Samples.SagaOrderFlow/Program.cs`

## Querying Saga State

Inject `ISagaRepository<TSaga>` and call `FindAsync` with the correlation id:

```csharp
app.MapGet("/orders/{id}/status", async (
    Guid id,
    ISagaRepository<OrderSagaState> repository,
    CancellationToken ct) =>
{
    OrderSagaState? saga = await repository.FindAsync(id, ct);
    return saga is null
        ? Results.NotFound()
        : Results.Ok(new { saga.CurrentState, saga.OrderId, saga.CreatedAt });
});
```

The repository also exposes `SaveAsync`, `UpdateAsync` (optimistic-concurrency checked via `Version`),
and `DeleteAsync`. Note that a `Finalize()`d saga is **deleted** — `FindAsync` then returns `null`,
which a terminal-state lookup should treat as "completed", not "never existed".

## Compensable Activities

For multi-step workflows that must roll back on failure, implement
`ICompensableActivity<TArguments, TLog>`. `ExecuteAsync` performs the step and returns a **log**
record capturing what it did; `CompensateAsync` consumes that log to undo it:

```csharp
public sealed record ReserveStockArguments(string OrderId, decimal Amount);
public sealed record ReserveStockLog(string OrderId, string ReservationId);

public sealed class ReserveStockActivity : ICompensableActivity<ReserveStockArguments, ReserveStockLog>
{
    public Task<ReserveStockLog> ExecuteAsync(ReserveStockArguments args, CancellationToken ct = default)
    {
        var reservationId = Guid.NewGuid().ToString();
        // reserve stock...
        return Task.FromResult(new ReserveStockLog(args.OrderId, reservationId));
    }

    public Task CompensateAsync(ReserveStockLog log, CancellationToken ct = default)
    {
        // release the reservation recorded in `log`...
        return Task.CompletedTask;
    }
}
```

The log type carries exactly the data compensation needs, so a later step's failure can unwind the
earlier steps in reverse using their recorded logs.

> See: `samples/BareWire.Samples.SagaOrderFlow/Activities/`

## See also

- [Redis Saga Persistence](saga-redis.md) — Redis-backed repository with optimistic concurrency
- [Transactional Outbox](outbox.md) — reliable publishing from saga side-effects
- [API Reference](../api/index.md)
