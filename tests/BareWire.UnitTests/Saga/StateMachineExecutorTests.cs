using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace BareWire.UnitTests.Saga;

// ── Event types ───────────────────────────────────────────────────────────────

public sealed record ExecutorOrderCreated(Guid OrderId);
public sealed record ExecutorOrderCompleted(Guid OrderId);
public sealed record ExecutorOrderCancelled(Guid OrderId);

// ── State machines ────────────────────────────────────────────────────────────

internal sealed class ExecutorStateMachine : BareWireStateMachine<ExecutorSagaState>
{
    public ExecutorStateMachine()
    {
        var orderCreated = Event<ExecutorOrderCreated>();
        var orderCompleted = Event<ExecutorOrderCompleted>();

        CorrelateBy<ExecutorOrderCreated>(e => e.OrderId);
        CorrelateBy<ExecutorOrderCompleted>(e => e.OrderId);
        CorrelateBy<ExecutorOrderCancelled>(e => e.OrderId);

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

/// <summary>
/// State machine that transitions in "Submitted" state without finalizing,
/// which means UpdateAsync is called. Used for concurrency retry tests.
/// </summary>
internal sealed class ExecutorStateMachineForUpdate : BareWireStateMachine<ExecutorSagaState>
{
    public ExecutorStateMachineForUpdate()
    {
        var orderCreated = Event<ExecutorOrderCreated>();
        CorrelateBy<ExecutorOrderCreated>(e => e.OrderId);

        During("Submitted", () =>
        {
            When(orderCreated, b => b.TransitionTo("Processing"));
        });
    }
}

/// <summary>
/// State machine that publishes a message on the Initially transition.
/// Used to verify that pending publish actions are executed after persist.
/// </summary>
internal sealed class ExecutorStateMachineWithPublish : BareWireStateMachine<ExecutorSagaState>
{
    public ExecutorStateMachineWithPublish()
    {
        var orderCreated = Event<ExecutorOrderCreated>();
        CorrelateBy<ExecutorOrderCreated>(e => e.OrderId);

        Initially(() =>
        {
            When(orderCreated, b => b
                .TransitionTo("Submitted")
                .Publish<ExecutorOrderCompleted>((saga, _) => new ExecutorOrderCompleted(saga.CorrelationId)));
        });
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class StateMachineExecutorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (StateMachineExecutor<ExecutorSagaState> Executor, ISagaRepository<ExecutorSagaState> Repository)
        CreateExecutor(int maxRetries = 3)
    {
        var sm = new ExecutorStateMachine();
        var definition = StateMachineDefinition<ExecutorSagaState>.Build(sm);
        var repository = Substitute.For<ISagaRepository<ExecutorSagaState>>();
        var logger = NullLogger<StateMachineExecutor<ExecutorSagaState>>.Instance;
        var executor = new StateMachineExecutor<ExecutorSagaState>(definition, repository, logger,
            scheduleProvider: null, maxRetries: maxRetries);
        return (executor, repository);
    }

    private static (StateMachineExecutor<ExecutorSagaState> Executor, ISagaRepository<ExecutorSagaState> Repository)
        CreateExecutorForUpdate(int maxRetries = 3)
    {
        var sm = new ExecutorStateMachineForUpdate();
        var definition = StateMachineDefinition<ExecutorSagaState>.Build(sm);
        var repository = Substitute.For<ISagaRepository<ExecutorSagaState>>();
        var logger = NullLogger<StateMachineExecutor<ExecutorSagaState>>.Instance;
        var executor = new StateMachineExecutor<ExecutorSagaState>(definition, repository, logger,
            scheduleProvider: null, maxRetries: maxRetries);
        return (executor, repository);
    }

    private static ConsumeContext CreateConsumeContext(IPublishEndpoint? publishEndpoint = null)
        => SagaTestHelpers.CreateConsumeContext(publishEndpoint: publishEndpoint);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessEvent_NewSaga_CreatesAndPersists()
    {
        var (executor, repository) = CreateExecutor();
        var orderId = Guid.NewGuid();
        repository.FindAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((ExecutorSagaState?)null);
        repository.SaveAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await executor.ProcessEventAsync(new ExecutorOrderCreated(orderId), CreateConsumeContext(),
            CancellationToken.None);

        await repository.Received(1).SaveAsync(
            Arg.Is<ExecutorSagaState>(s => s.CorrelationId == orderId && s.CurrentState == "Submitted"),
            Arg.Any<CancellationToken>());
        await repository.DidNotReceive().UpdateAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEvent_ExistingSaga_TransitionsState()
    {
        var (executor, repository) = CreateExecutor();
        var orderId = Guid.NewGuid();
        var existingSaga = new ExecutorSagaState
        {
            CorrelationId = orderId,
            CurrentState = "Submitted",
            Version = 1
        };
        repository.FindAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(existingSaga);
        repository.DeleteAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // OrderCompleted in Submitted state triggers TransitionTo("Completed").Finalize() → DeleteAsync
        await executor.ProcessEventAsync(new ExecutorOrderCompleted(orderId), CreateConsumeContext(),
            CancellationToken.None);

        await repository.Received(1).DeleteAsync(orderId, Arg.Any<CancellationToken>());
        await repository.DidNotReceive().UpdateAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEvent_UnhandledEvent_IsIgnored()
    {
        var (executor, repository) = CreateExecutor();
        var orderId = Guid.NewGuid();
        var existingSaga = new ExecutorSagaState
        {
            CorrelationId = orderId,
            CurrentState = "Submitted",
            Version = 1
        };
        repository.FindAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(existingSaga);

        // ExecutorOrderCancelled has no handler in "Submitted" state
        await executor.ProcessEventAsync(new ExecutorOrderCancelled(orderId), CreateConsumeContext(),
            CancellationToken.None);

        await repository.DidNotReceive().SaveAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>());
        await repository.DidNotReceive().UpdateAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>());
        await repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEvent_ConcurrencyConflict_RetriesSuccessfully()
    {
        var (executor, repository) = CreateExecutorForUpdate(maxRetries: 3);
        var orderId = Guid.NewGuid();
        int findCallCount = 0;
        int updateCallCount = 0;

        repository.FindAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                findCallCount++;
                return Task.FromResult<ExecutorSagaState?>(new ExecutorSagaState
                {
                    CorrelationId = orderId,
                    CurrentState = "Submitted",
                    Version = findCallCount
                });
            });

        repository.UpdateAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                updateCallCount++;
                return updateCallCount == 1
                    ? Task.FromException(new ConcurrencyException(typeof(ExecutorSagaState), orderId, 1, 2))
                    : Task.CompletedTask;
            });

        // ExecutorOrderCreated in "Submitted" triggers TransitionTo("Processing") without finalize → UpdateAsync
        await executor.ProcessEventAsync(
            new ExecutorOrderCreated(orderId), CreateConsumeContext(), CancellationToken.None);

        updateCallCount.Should().Be(2);
        findCallCount.Should().Be(2);
        await repository.Received(2).UpdateAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEvent_MaxRetriesExceeded_ThrowsConcurrencyException()
    {
        var (executor, repository) = CreateExecutorForUpdate(maxRetries: 2);
        var orderId = Guid.NewGuid();

        repository.FindAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ExecutorSagaState?>(new ExecutorSagaState
            {
                CorrelationId = orderId,
                CurrentState = "Submitted",
                Version = 1
            }));

        repository.UpdateAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new ConcurrencyException(typeof(ExecutorSagaState), orderId, 1, 2)));

        Func<Task> act = () => executor.ProcessEventAsync(
            new ExecutorOrderCreated(orderId), CreateConsumeContext(), CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    [Fact]
    public async Task ProcessEvent_Finalize_DeletesSaga()
    {
        var (executor, repository) = CreateExecutor();
        var orderId = Guid.NewGuid();
        var existingSaga = new ExecutorSagaState
        {
            CorrelationId = orderId,
            CurrentState = "Submitted",
            Version = 1
        };
        repository.FindAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(existingSaga);
        repository.DeleteAsync(orderId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await executor.ProcessEventAsync(new ExecutorOrderCompleted(orderId), CreateConsumeContext(),
            CancellationToken.None);

        await repository.Received(1).DeleteAsync(orderId, Arg.Any<CancellationToken>());
        await repository.DidNotReceive().UpdateAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEvent_WithPublish_ExecutesPendingActions()
    {
        var sm = new ExecutorStateMachineWithPublish();
        var definition = StateMachineDefinition<ExecutorSagaState>.Build(sm);
        var repository = Substitute.For<ISagaRepository<ExecutorSagaState>>();
        var logger = NullLogger<StateMachineExecutor<ExecutorSagaState>>.Instance;
        var executor = new StateMachineExecutor<ExecutorSagaState>(definition, repository, logger);

        var orderId = Guid.NewGuid();
        repository.FindAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((ExecutorSagaState?)null);
        repository.SaveAsync(Arg.Any<ExecutorSagaState>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.PublishAsync(Arg.Any<ExecutorOrderCompleted>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var consumeContext = CreateConsumeContext(publishEndpoint: publishEndpoint);

        await executor.ProcessEventAsync(new ExecutorOrderCreated(orderId), consumeContext,
            CancellationToken.None);

        await publishEndpoint.Received(1)
            .PublishAsync(Arg.Any<ExecutorOrderCompleted>(), Arg.Any<CancellationToken>());
    }
}
