using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga;

namespace BareWire.UnitTests.Saga;

// ── Test state machines ───────────────────────────────────────────────────────

internal sealed class OrderStateMachine : BareWireStateMachine<OrderSagaState>
{
    public OrderStateMachine()
    {
        State("Submitted");
        State("Completed");

        var orderCreated = Event<OrderCreated>();
        var orderCompleted = Event<OrderCompleted>();

        CorrelateBy<OrderCreated>(e => e.OrderId);
        CorrelateBy<OrderCompleted>(e => e.OrderId);

        Initially(() =>
        {
            When(orderCreated, b => b.TransitionTo("Submitted"));
        });

        During("Submitted", () =>
        {
            When(orderCompleted, b => b.TransitionTo("Completed").Finalize());
        });
    }
}

internal sealed class OrderStateMachineWithDuringAny : BareWireStateMachine<OrderSagaState>
{
    public OrderStateMachineWithDuringAny()
    {
        var orderCreated = Event<OrderCreated>();
        var orderCancelled = Event<OrderCancelled>();

        CorrelateBy<OrderCreated>(e => e.OrderId);
        CorrelateBy<OrderCancelled>(e => e.OrderId);

        Initially(() =>
        {
            When(orderCreated, b => b.TransitionTo("Submitted"));
        });

        DuringAny(() =>
        {
            When(orderCancelled, b => b.TransitionTo("Cancelled").Finalize());
        });
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class StateMachineDefinitionTests
{
    [Fact]
    public void Build_SimpleStateMachine_CapturesStatesAndEvents()
    {
        var sm = new OrderStateMachine();

        var definition = StateMachineDefinition<OrderSagaState>.Build(sm);

        // Initially-registered events are found in "Initial" state
        var initialActivities = definition.GetActivities<OrderCreated>("Initial");
        initialActivities.Should().NotBeNull();
        initialActivities!.Count.Should().Be(1);

        // During("Submitted")-registered events are found in "Submitted" state
        var submittedActivities = definition.GetActivities<OrderCompleted>("Submitted");
        submittedActivities.Should().NotBeNull();
        submittedActivities!.Count.Should().Be(2); // TransitionTo + Finalize
    }

    [Fact]
    public void Build_WithCorrelation_CapturesCorrelationMapping()
    {
        var orderId = Guid.NewGuid();
        var sm = new OrderStateMachine();

        var definition = StateMachineDefinition<OrderSagaState>.Build(sm);

        var correlationId = definition.GetCorrelationId(new OrderCreated(orderId, "Alice"));

        correlationId.Should().Be(orderId);
    }

    [Fact]
    public void Build_WithDuringAny_AppliesToAllStates()
    {
        var sm = new OrderStateMachineWithDuringAny();

        var definition = StateMachineDefinition<OrderSagaState>.Build(sm);

        // DuringAny applies in "Initial" state
        var initialActivities = definition.GetActivities<OrderCancelled>("Initial");
        initialActivities.Should().NotBeNull();
        initialActivities!.Count.Should().BeGreaterThan(0);

        // DuringAny also applies in non-initial states
        var submittedActivities = definition.GetActivities<OrderCancelled>("Submitted");
        submittedActivities.Should().NotBeNull();
        submittedActivities!.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetActivities_ForRegisteredStateAndEvent_ReturnsActivities()
    {
        var sm = new OrderStateMachine();
        var definition = StateMachineDefinition<OrderSagaState>.Build(sm);

        var activities = definition.GetActivities<OrderCompleted>("Submitted");

        activities.Should().NotBeNull();
        activities!.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetActivities_ForUnregisteredEvent_ReturnsNull()
    {
        var sm = new OrderStateMachine();
        var definition = StateMachineDefinition<OrderSagaState>.Build(sm);

        // OrderCancelled is not registered in this state machine
        var activities = definition.GetActivities<OrderCancelled>("Submitted");

        activities.Should().BeNull();
    }

    [Fact]
    public void GetCorrelationId_ValidMapping_ReturnsGuid()
    {
        var expectedId = Guid.NewGuid();
        var sm = new OrderStateMachine();
        var definition = StateMachineDefinition<OrderSagaState>.Build(sm);

        var result = definition.GetCorrelationId(new OrderCompleted(expectedId));

        result.Should().Be(expectedId);
    }

    [Fact]
    public void GetCorrelationId_MissingMapping_ThrowsSagaException()
    {
        var sm = new OrderStateMachine();
        var definition = StateMachineDefinition<OrderSagaState>.Build(sm);

        // OrderCancelled has no CorrelateBy registered in OrderStateMachine
        Action act = () => definition.GetCorrelationId(new OrderCancelled(Guid.NewGuid()));

        act.Should().Throw<BareWireSagaException>()
            .WithMessage("*OrderCancelled*");
    }
}
