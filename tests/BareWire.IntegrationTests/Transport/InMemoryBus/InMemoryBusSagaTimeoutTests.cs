using AwesomeAssertions;
using BareWire.Abstractions.Saga;
using BareWire.Saga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>
/// Scenario 7 (task 20.28): a SAGA timeout scheduled on the in-memory transport's native message
/// scheduler (task 20.25) is delivered back to the SAGA's own queue and drives a state transition,
/// with no runtime topology deployment (the in-memory topology is sealed at bus startup).
/// </summary>
/// <remarks>
/// Timeout CANCELLATION through the SAGA engine is a known, separate limitation (task 20.25) — this
/// test asserts only that a scheduled timeout is DELIVERED and drives a transition, never that
/// cancellation works end to end.
/// </remarks>
[Collection(InMemoryBusIsolation.Name)]
public sealed class InMemoryBusSagaTimeoutTests
{
    private const string SagaQueue = "saga-timeout";
    private static readonly TimeSpan TimeoutDelay = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task SagaTimeout_ScheduledOnInMemoryBus_IsDeliveredAndTransitionsSagaWithoutRuntimeTopologyDeploy()
    {
        // GAP-6 mitigation: InMemorySagaRepository<TSaga> is internal and not registered by default —
        // register it explicitly, and hold the exact same instance so the test polls the state the
        // dispatcher actually wrote, not a second, unrelated repository.
        var repository = new InMemorySagaRepository<TimeoutSagaState>();

        await using InMemoryBusHost host = await InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.AutoDeclareEndpointQueues();
                t.MapRoutingKey<Started>(SagaQueue);
                t.ReceiveEndpoint(SagaQueue, e => e.StateMachineSaga<TimeoutSagaStateMachine>());
            },
            services: s => s
                .AddSingleton<ISagaRepository<TimeoutSagaState>>(repository)
                .AddBareWireSagaStateMachine<TimeoutSagaStateMachine, TimeoutSagaState>(),
            cancellationToken: TestContext.Current.CancellationToken);

        Guid correlationId = Guid.NewGuid();
        await host.Bus.PublishAsync(new Started(correlationId), TestContext.Current.CancellationToken);

        TimeoutSagaState? state = null;
        await WaitUntilAsync(
            async ct =>
            {
                state = await repository.FindAsync(correlationId, ct).ConfigureAwait(false);
                return state is { CurrentState: "Expired" };
            },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        state.Should().NotBeNull();
        state!.CurrentState.Should().Be("Expired");

        // No topology/deploy error surfaced on the bus while scheduling or delivering the timeout.
        host.Telemetry.HasLog(LogLevel.Error, string.Empty).Should().BeFalse();
    }

    private static async Task WaitUntilAsync(
        Func<CancellationToken, Task<bool>> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            while (!await condition(linkedCts.Token).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timed out — fall through so the caller's own assertion reports the unmet condition.
        }
    }

    // ── SAGA state, events, and state machine (local to this test — no production changes) ──────────

    private sealed class TimeoutSagaState : ISagaState
    {
        public Guid CorrelationId { get; set; }

        public string CurrentState { get; set; } = "Initial";

        public int Version { get; set; }
    }

    private sealed record Started(Guid Id);

    private sealed record TimedOut(Guid Id);

    private sealed class TimeoutSagaStateMachine : BareWireStateMachine<TimeoutSagaState>
    {
        public TimeoutSagaStateMachine()
        {
            EventHandle<Started> started = Event<Started>();
            EventHandle<TimedOut> timedOut = Event<TimedOut>();

            StateHandle waiting = State("Waiting");
            StateHandle expired = State("Expired");

            CorrelateBy<Started>(e => e.Id);
            CorrelateBy<TimedOut>(e => e.Id);

            ScheduleHandle<TimedOut> timeoutSchedule = Schedule<TimedOut>(c => c.Delay = TimeoutDelay);

            Initially(() =>
            {
                When(started, b => b
                    .ScheduleTimeout(static (_, evt) => new TimedOut(evt.Id), timeoutSchedule)
                    .TransitionTo(waiting.Name));
            });

            During(waiting, () =>
            {
                When(timedOut, b => b.TransitionTo(expired.Name));
            });
        }
    }
}
