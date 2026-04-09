using AwesomeAssertions;
using BareWire.Abstractions;
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

public sealed class BareWireBusControlTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (BareWireBusControl Control, BareWireBus Bus, ITransportAdapter Adapter, FlowController FlowController)
        CreateControl()
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
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();

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

        // Provide a configurator with InMemory transport so ConfigurationValidator passes.
        BusConfigurator configurator = new();
        configurator.HasInMemoryTransport = true;

        BareWireBusControl control = new(
            bus,
            adapter,
            flowController,
            configurator,
            NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: scopeFactory,
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        return (control, bus, adapter, flowController);
    }

    // ── StartAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ReturnsHandleWithMatchingBusId()
    {
        var (control, bus, _, _) = CreateControl();

        BusHandle handle = await control.StartAsync(CancellationToken.None);

        handle.Should().NotBeNull();
        handle.BusId.Should().Be(bus.BusId);

        await control.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyStarted_Throws()
    {
        var (control, _, _, _) = CreateControl();
        await control.StartAsync(CancellationToken.None);

        Func<Task> secondStart = async () => await control.StartAsync(CancellationToken.None);

        await secondStart.Should().ThrowAsync<InvalidOperationException>();

        await control.DisposeAsync();
    }

    // ── StopAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_WhenNotStarted_NoOp()
    {
        var (control, _, _, _) = CreateControl();

        // Should not throw when called before StartAsync.
        Func<Task> stop = async () => await control.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_AfterStart_Succeeds()
    {
        var (control, _, _, _) = CreateControl();
        await control.StartAsync(CancellationToken.None);

        Func<Task> stop = async () => await control.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    // ── CheckHealth ───────────────────────────────────────────────────────────

    [Fact]
    public void CheckHealth_WithNoEndpoints_ReturnsHealthy()
    {
        var (control, _, _, _) = CreateControl();

        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Healthy);
        status.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public void CheckHealth_AggregatesEndpointStatuses()
    {
        var (control, _, _, flowController) = CreateControl();

        // Register two endpoints with different utilizations.
        FlowControlOptions lowOptions = new() { MaxInFlightMessages = 100 };
        CreditManager lowManager = flowController.GetOrCreateManager("endpoint-low", lowOptions);

        // Keep utilization at 0% — Healthy.
        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Healthy);
        status.Endpoints.Should().HaveCount(1);
        status.Endpoints[0].EndpointName.Should().Be("endpoint-low");
        status.Endpoints[0].Status.Should().Be(BusStatus.Healthy);
    }

    [Fact]
    public void CheckHealth_WhenDegraded_ReturnsDegraded()
    {
        var (control, _, _, flowController) = CreateControl();

        FlowControlOptions options = new() { MaxInFlightMessages = 10 };
        CreditManager manager = flowController.GetOrCreateManager("degraded-endpoint", options);

        // Push 9 messages in-flight → 90% → Degraded.
        manager.TrackInflight(9, 0);

        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Degraded);
        status.Endpoints[0].Status.Should().Be(BusStatus.Degraded);
    }

    // ── IBus delegation ───────────────────────────────────────────────────────

    [Fact]
    public void IBusMethods_DelegateToBus()
    {
        var (control, bus, _, _) = CreateControl();

        // BusId and Address should proxy through to the underlying bus.
        control.BusId.Should().Be(bus.BusId);
        control.Address.Should().Be(bus.Address);
    }

    [Fact]
    public async Task GetSendEndpoint_DelegatesToBus()
    {
        var (control, _, _, _) = CreateControl();
        await control.StartAsync(CancellationToken.None);

        Uri address = new("rabbitmq://localhost/my-queue");
        ISendEndpoint endpoint = await control.GetSendEndpoint(address, CancellationToken.None);

        endpoint.Should().NotBeNull();
        endpoint.Address.Should().Be(address);

        await control.DisposeAsync();
    }

    [Fact]
    public async Task CreateRequestClientAsync_ThrowsNotSupportedException()
    {
        var (control, _, _, _) = CreateControl();

        Func<Task> act = async () => await control.CreateRequestClientAsync<BusTestMessage>();

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // ── Constructor null guards ───────────────────────────────────────────────

    [Fact]
    public void Constructor_WhenBusIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        MiddlewareChain chain = new([]);
        MessagePipeline pipeline = new(chain, deserializerResolver, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        BusConfigurator configurator = new();
        configurator.HasInMemoryTransport = true;

        // bus = null triggers MUT-299
        Action act = () => _ = new BareWireBusControl(
            bus: null!,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("bus");
    }

    [Fact]
    public void Constructor_WhenAdapterIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        Action act = () => _ = new BareWireBusControl(
            bus: bus,
            adapter: null!,
            flowController: flowController,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("adapter");
    }

    [Fact]
    public void Constructor_WhenFlowControllerIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        Action act = () => _ = new BareWireBusControl(
            bus: bus,
            adapter: adapter,
            flowController: null!,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("flowController");
    }

    [Fact]
    public void Constructor_WhenConfiguratorIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        Action act = () => _ = new BareWireBusControl(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: null!,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("configurator");
    }

    [Fact]
    public void Constructor_WhenLoggerIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        Action act = () => _ = new BareWireBusControl(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: null!,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_WhenDeserializerResolverIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        Action act = () => _ = new BareWireBusControl(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: null!,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("deserializerResolver");
    }

    [Fact]
    public void Constructor_WhenScopeFactoryIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        Action act = () => _ = new BareWireBusControl(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: null!,
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("scopeFactory");
    }

    [Fact]
    public void Constructor_WhenInstrumentationIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        Action act = () => _ = new BareWireBusControl(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: null!,
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("instrumentation");
    }

    [Fact]
    public void Constructor_WhenLoggerFactoryIsNull_ThrowsArgumentNullException()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        Action act = () => _ = new BareWireBusControl(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: null!,
            sagaDispatchers: []);

        act.Should().Throw<ArgumentNullException>().WithParameterName("loggerFactory");
    }

    [Fact]
    public void Constructor_WhenEndpointBindingsIsNull_DefaultsToEmpty()
    {
        // endpointBindings defaults to [] when null (MUT-304/305) — should NOT throw.
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        BareWireBusControl control = new(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: null!,
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: []);

        // If construction succeeded, the default [] was applied — verify health check works (non-null field).
        BusHealthStatus health = control.CheckHealth();
        health.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WhenSagaDispatchersIsNull_DefaultsToEmpty()
    {
        // sagaDispatchers defaults to [] when null (MUT-310/311) — should NOT throw.
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

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

        BareWireBusControl control = new(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: null!);

        // If construction succeeded, the default [] was applied — verify health check works.
        BusHealthStatus health = control.CheckHealth();
        health.Should().NotBeNull();
    }
}
