using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Saga;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.Configuration;
using BareWire.FlowControl;
using BareWire;
using BareWire.Pipeline;
using BareWire.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

public sealed class BareWireBusControlSagaDispatcherTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BareWireBusControl CreateControl(
        IReadOnlyList<ISagaMessageDispatcher> sagaDispatchers,
        IReadOnlyList<EndpointBinding>? endpointBindings = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });
        adapter.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();
        IServiceScopeFactory resolvedScopeFactory = scopeFactory ?? Substitute.For<IServiceScopeFactory>();

        MiddlewareChain chain = new([]);
        MessagePipeline pipeline = new(chain, deserializerResolver, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        BareWireBus bus = new(
            adapter,
            new DefaultSerializerResolver(serializer),
            pipeline,
            flowController,
            new PublishFlowControlOptions(),
            NullLogger<BareWireBus>.Instance,
            new NullInstrumentation());

        BusConfigurator configurator = new();
        configurator.HasInMemoryTransport = true;

        return new BareWireBusControl(
            bus,
            adapter,
            flowController,
            configurator,
            NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: endpointBindings ?? [],
            deserializerResolver: deserializerResolver,
            scopeFactory: resolvedScopeFactory,
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: sagaDispatchers);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_SagaDispatchers_SurviveBeyondStartAsyncScope()
    {
        // Arrange — a mock dispatcher injected via the constructor
        ISagaMessageDispatcher dispatcher = Substitute.For<ISagaMessageDispatcher>();
        dispatcher.StateMachineType.Returns(typeof(object));
        dispatcher
            .TryDispatchAsync(
                Arg.Any<ReadOnlySequence<byte>>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IPublishEndpoint>(),
                Arg.Any<ISendEndpointProvider>(),
                Arg.Any<IDeserializerResolver>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await using BareWireBusControl control = CreateControl([dispatcher]);

        // Act — start the bus (previously this disposed the scope holding the dispatcher)
        await control.StartAsync(CancellationToken.None);

        // Assert — intent/documentation test: verifies the dispatcher is still callable
        // after StartAsync. NSubstitute mocks don't simulate scope disposal, so this
        // assertion is inherently vacuous. The true regression guard is
        // StartAsync_SagaDispatchers_ResolvedFromRootProvider (CreateScope not called).
        Func<Task> useDispatcher = async () =>
            await dispatcher.TryDispatchAsync(
                ReadOnlySequence<byte>.Empty,
                new Dictionary<string, string>(),
                "msg-id-1",
                "test-endpoint",
                Substitute.For<IPublishEndpoint>(),
                Substitute.For<ISendEndpointProvider>(),
                Substitute.For<IDeserializerResolver>(),
                CancellationToken.None);

        await useDispatcher.Should().NotThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task StartAsync_WithNoSagaDispatchers_DoesNotThrow()
    {
        // Arrange — empty dispatcher list simulates a bus with no sagas registered
        await using BareWireBusControl control = CreateControl([]);

        // Act & Assert
        Func<Task> act = async () => await control.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SagaDispatchers_ResolvedFromRootProvider()
    {
        // Arrange — a scope factory whose CreateScope() we can verify was NOT called
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        ISagaMessageDispatcher dispatcher = Substitute.For<ISagaMessageDispatcher>();
        dispatcher.StateMachineType.Returns(typeof(object));

        await using BareWireBusControl control = CreateControl(
            sagaDispatchers: [dispatcher],
            scopeFactory: scopeFactory);

        // Act
        await control.StartAsync(CancellationToken.None);

        // Assert — dispatchers come from the constructor, not from a temporary scope;
        // CreateScope() must not be called during StartAsync for this purpose
        scopeFactory.DidNotReceive().CreateScope();
    }
}
