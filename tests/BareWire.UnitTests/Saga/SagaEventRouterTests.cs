using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

// ── Event types for SagaEventRouter tests ─────────────────────────────────────

public sealed record RouterOrderCreated(Guid OrderId);
public sealed record RouterUnregisteredEvent(Guid OrderId);

// ── State machines ────────────────────────────────────────────────────────────

internal sealed class RouterStateMachine : BareWireStateMachine<RouterSagaState>
{
    public RouterStateMachine()
    {
        var orderCreated = Event<RouterOrderCreated>();
        CorrelateBy<RouterOrderCreated>(e => e.OrderId);

        Initially(() =>
        {
            When(orderCreated, b => b.TransitionTo("Submitted"));
        });
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class SagaEventRouterTests
{
    private static (SagaEventRouter Router, ISagaRepository<RouterSagaState> Repository)
        CreateRouter()
    {
        var sm = new RouterStateMachine();
        var definition = StateMachineDefinition<RouterSagaState>.Build(sm);
        var repository = Substitute.For<ISagaRepository<RouterSagaState>>();
        var logger = NullLogger<StateMachineExecutor<RouterSagaState>>.Instance;
        var executor = new StateMachineExecutor<RouterSagaState>(definition, repository, logger);

        var router = new SagaEventRouter();
        router.Register<RouterSagaState, RouterOrderCreated>(executor);

        return (router, repository);
    }

    [Fact]
    public async Task RouteAsync_RegisteredEvent_RoutesToExecutor()
    {
        var (router, repository) = CreateRouter();
        var orderId = Guid.NewGuid();

        repository.FindAsync(orderId, Arg.Any<CancellationToken>())
            .Returns((RouterSagaState?)null);
        repository.SaveAsync(Arg.Any<RouterSagaState>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = SagaTestHelpers.CreateConsumeContext();

        await router.RouteAsync(new RouterOrderCreated(orderId), context, CancellationToken.None);

        await repository.Received(1).SaveAsync(
            Arg.Is<RouterSagaState>(s => s.CorrelationId == orderId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteAsync_UnregisteredEvent_ThrowsSagaException()
    {
        var (router, _) = CreateRouter();
        var context = SagaTestHelpers.CreateConsumeContext();

        Func<Task> act = () => router.RouteAsync(
            new RouterUnregisteredEvent(Guid.NewGuid()), context, CancellationToken.None);

        await act.Should().ThrowAsync<BareWireSagaException>()
            .WithMessage("*RouterUnregisteredEvent*");
    }

    [Fact]
    public async Task RouteAsync_NullEvent_ThrowsArgumentNullException()
    {
        var (router, _) = CreateRouter();
        var context = SagaTestHelpers.CreateConsumeContext();

        Func<Task> act = () => router.RouteAsync<RouterOrderCreated>(null!, context, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RouteAsync_NullContext_ThrowsArgumentNullException()
    {
        var (router, _) = CreateRouter();

        Func<Task> act = () => router.RouteAsync(
            new RouterOrderCreated(Guid.NewGuid()), null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
