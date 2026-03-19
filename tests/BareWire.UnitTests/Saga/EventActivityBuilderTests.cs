using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using BareWire.Saga;
using BareWire.Saga.Activities;

namespace BareWire.UnitTests.Saga;

public sealed class EventActivityBuilderTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class TestSagaState : ISagaState
    {
        public Guid CorrelationId { get; set; }
        public string CurrentState { get; set; } = "Initial";
        public int Version { get; set; }
    }

    private sealed record TestEvent(string Value);
    private sealed record TestMessage(string Content);
    private sealed record TestTimeout();

    private static ConsumeContext CreateConsumeContext()
        => SagaTestHelpers.CreateConsumeContext();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TransitionTo_AddsTransitionStep()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();

        builder.TransitionTo("Submitted");

        builder.Steps.Count.Should().Be(1);
        builder.Steps[0].Should().BeOfType<TransitionActivity<TestSagaState, TestEvent>>();
    }

    [Fact]
    public void Then_AddsActionStep()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();

        builder.Then((_, _) => Task.CompletedTask);

        builder.Steps.Count.Should().Be(1);
        builder.Steps[0].Should().BeOfType<ThenActivity<TestSagaState, TestEvent>>();
    }

    [Fact]
    public void Publish_AddsPublishStep()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();

        builder.Publish<TestMessage>((_, _) => new TestMessage("hello"));

        builder.Steps.Count.Should().Be(1);
        builder.Steps[0].Should().BeOfType<PublishActivity<TestSagaState, TestEvent, TestMessage>>();
    }

    [Fact]
    public void Send_AddsSendStep()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();
        var destination = new Uri("rabbitmq://localhost/orders");

        builder.Send<TestMessage>(destination, (_, _) => new TestMessage("hello"));

        builder.Steps.Count.Should().Be(1);
        builder.Steps[0].Should().BeOfType<SendActivity<TestSagaState, TestEvent, TestMessage>>();
    }

    [Fact]
    public void FluentChain_PreservesOrder()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();
        var destination = new Uri("rabbitmq://localhost/orders");

        builder
            .Then((_, _) => Task.CompletedTask)
            .Publish<TestMessage>((_, _) => new TestMessage("x"))
            .Send<TestMessage>(destination, (_, _) => new TestMessage("y"))
            .TransitionTo("Submitted")
            .Finalize();

        builder.Steps.Count.Should().Be(5);
        builder.Steps[0].Should().BeOfType<ThenActivity<TestSagaState, TestEvent>>();
        builder.Steps[1].Should().BeOfType<PublishActivity<TestSagaState, TestEvent, TestMessage>>();
        builder.Steps[2].Should().BeOfType<SendActivity<TestSagaState, TestEvent, TestMessage>>();
        builder.Steps[3].Should().BeOfType<TransitionActivity<TestSagaState, TestEvent>>();
        builder.Steps[4].Should().BeOfType<FinalizeActivity<TestSagaState, TestEvent>>();
    }

    [Fact]
    public void Finalize_AddsFinalizeStep()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();

        builder.Finalize();

        builder.Steps.Count.Should().Be(1);
        builder.Steps[0].Should().BeOfType<FinalizeActivity<TestSagaState, TestEvent>>();
    }

    [Fact]
    public void ScheduleTimeout_AddsScheduleStep()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();

        builder.ScheduleTimeout<TestTimeout>((_, _) => new TestTimeout());

        builder.Steps.Count.Should().Be(1);
        builder.Steps[0].Should().BeOfType<ScheduleTimeoutActivity<TestSagaState, TestEvent, TestTimeout>>();
    }

    [Fact]
    public void CancelTimeout_AddsCancelStep()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();

        builder.CancelTimeout<TestTimeout>();

        builder.Steps.Count.Should().Be(1);
        builder.Steps[0].Should().BeOfType<CancelTimeoutActivity<TestSagaState, TestEvent, TestTimeout>>();
    }

    [Fact]
    public async Task TransitionTo_WhenExecuted_SetsTargetStateOnContext()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();
        builder.TransitionTo("Submitted");

        var saga = new TestSagaState { CorrelationId = Guid.NewGuid() };
        var @event = new TestEvent("test");
        var consumeContext = CreateConsumeContext();
        var context = new BehaviorContext<TestSagaState, TestEvent>(saga, @event, consumeContext);

        await builder.Steps[0].ExecuteAsync(context, CancellationToken.None);

        context.TargetState.Should().Be("Submitted");
    }

    [Fact]
    public async Task Finalize_WhenExecuted_SetsShouldFinalizeOnContext()
    {
        var builder = new EventActivityBuilder<TestSagaState, TestEvent>();
        builder.Finalize();

        var saga = new TestSagaState { CorrelationId = Guid.NewGuid() };
        var @event = new TestEvent("test");
        var consumeContext = CreateConsumeContext();
        var context = new BehaviorContext<TestSagaState, TestEvent>(saga, @event, consumeContext);

        await builder.Steps[0].ExecuteAsync(context, CancellationToken.None);

        context.ShouldFinalize.Should().BeTrue();
    }
}
