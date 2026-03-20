using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Core.Bus;
using BareWire.Core.Configuration;
using BareWire.Core.FlowControl;
using BareWire.Core.Observability;
using BareWire.Core.Pipeline;
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

        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();

        MiddlewareChain chain = new([]);
        ConsumerDispatcher dispatcher = new(scopeFactory, NullLogger<ConsumerDispatcher>.Instance);
        MessagePipeline pipeline = new(chain, dispatcher, deserializer, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        BareWireBus bus = new(
            adapter,
            serializer,
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
            deserializer: deserializer,
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
}
