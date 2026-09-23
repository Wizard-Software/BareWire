using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.Configuration;
using BareWire.FlowControl;
using BareWire.Pipeline;
using BareWire.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Unit tests for the merge between <see cref="ITransportHealthSource"/> and
/// <see cref="BareWireBusControl.CheckHealth"/> — verifies that transport-reported queue health is
/// folded into the aggregated bus health without changing the no-seam behavior.
/// </summary>
public sealed class BareWireBusControlHealthSourceTests
{
    private const string QueueDescription = "Queue 'orders' is at 92% of capacity (920/1000).";

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static (BareWireBusControl Control, FlowController FlowController) CreateControl(ITransportAdapter adapter)
    {
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

        return (control, flowController);
    }

    // ── CheckHealth ───────────────────────────────────────────────────────────

    [Fact]
    public void CheckHealth_WhenTransportHealthSourceReportsDegraded_ReturnsDegradedWithQueueDescription()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter, ITransportHealthSource>();
        ((ITransportHealthSource)adapter).GetHealth().Returns(new BusHealthStatus(
            BusStatus.Degraded, QueueDescription,
            [new EndpointHealthStatus("orders", BusStatus.Degraded, QueueDescription)]));
        var (control, _) = CreateControl(adapter);

        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Degraded);
        status.Description.Should().Contain("orders").And.Contain("92%");
        status.Endpoints.Should().ContainSingle(e => e.EndpointName == "orders" && e.Status == BusStatus.Degraded);
    }

    [Fact]
    public void CheckHealth_WhenAdapterHasNoHealthSource_BehavesAsBefore()
    {
        var (control, _) = CreateControl(Substitute.For<ITransportAdapter>());

        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Healthy);
        status.Description.Should().Be("All endpoints are operating normally.");
        status.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public void CheckHealth_WhenTransportHealthSourceHealthy_KeepsDescriptionUnchanged()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter, ITransportHealthSource>();
        ((ITransportHealthSource)adapter).GetHealth().Returns(new BusHealthStatus(
            BusStatus.Healthy, "Transport idle.", []));
        var (control, _) = CreateControl(adapter);

        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Healthy);
        status.Description.Should().Be("All endpoints are operating normally.");
        status.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public void CheckHealth_WhenFlowControlDegradedAndTransportHealthy_DoesNotAppendTransportDescription()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter, ITransportHealthSource>();
        ((ITransportHealthSource)adapter).GetHealth().Returns(new BusHealthStatus(
            BusStatus.Healthy, "Transport idle.", []));
        var (control, flowController) = CreateControl(adapter);
        CreditManager manager = flowController.GetOrCreateManager(
            "busy-endpoint", new FlowControlOptions { MaxInFlightMessages = 10 });
        manager.TrackInflight(9, 0);

        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Degraded);
        status.Description.Should().Be("One or more endpoints are approaching capacity.");
    }

    [Fact]
    public void CheckHealth_WhenTransportUnhealthyAndFlowControlDegraded_ReturnsWorstStatus()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter, ITransportHealthSource>();
        const string unhealthyDescription = "Queue 'orders' is full (1000/1000).";
        ((ITransportHealthSource)adapter).GetHealth().Returns(new BusHealthStatus(
            BusStatus.Unhealthy, unhealthyDescription,
            [new EndpointHealthStatus("orders", BusStatus.Unhealthy, unhealthyDescription)]));
        var (control, flowController) = CreateControl(adapter);

        FlowControlOptions options = new() { MaxInFlightMessages = 10 };
        CreditManager manager = flowController.GetOrCreateManager("ep", options);
        manager.TrackInflight(9, 0);

        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Unhealthy);
        status.Endpoints.Should().HaveCount(2);
        status.Description.Should().Contain("orders");
    }

    [Fact]
    public void CheckHealth_WhenTransportAggregateHealthyButEndpointDegraded_AppendsTransportDescription()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter, ITransportHealthSource>();
        ((ITransportHealthSource)adapter).GetHealth().Returns(new BusHealthStatus(
            BusStatus.Healthy, QueueDescription,
            [new EndpointHealthStatus("orders", BusStatus.Degraded, QueueDescription)]));
        var (control, _) = CreateControl(adapter);

        BusHealthStatus status = control.CheckHealth();

        status.Status.Should().Be(BusStatus.Degraded);
        status.Description.Should().Contain("orders");
    }
}
