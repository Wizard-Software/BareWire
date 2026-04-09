using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Configuration;
using BareWire.Interop.MassTransit;
using BareWire.Serialization.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Interop;

/// <summary>
/// End-to-end tests for the publish-only bridge scenario:
/// <c>AddBareWire(bus =&gt; bus.MapSerializer&lt;T, MassTransitEnvelopeSerializer&gt;())</c>
/// without declaring any receive endpoint.
/// </summary>
public sealed class MassTransitEnvelope_PublishOnlyBridgeTests
{
    private static (IServiceProvider Services, List<OutboundMessage> Captured) BuildServiceProvider(
        Action<IBusConfigurator> configurebus)
    {
        ServiceCollection services = new();

        // Serializers
        services.AddBareWireJsonSerializer();
        services.AddMassTransitEnvelopeSerializer();

        // In-memory transport stub
        List<OutboundMessage> captured = [];

        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   captured.AddRange(messages);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });

        services.AddSingleton(adapter);

        // BareWire core with per-type serializer mapping
        services.AddBareWire(configurebus);

        // Mark the in-memory transport as registered (skips ConfigurationValidator transport check).
        // This is the same pattern used by BareWireTestHarness.
        ServiceDescriptor? configuratorDescriptor = services
            .FirstOrDefault(d => d.ServiceType == typeof(BusConfigurator));
        if (configuratorDescriptor?.ImplementationInstance is BusConfigurator busConfigurator)
            busConfigurator.HasInMemoryTransport = true;

        // Provide logger factory to satisfy internal dependencies
        services.AddLogging();

        return (services.BuildServiceProvider(), captured);
    }

    [Fact]
    public async Task PublishOnlyBridge_WhenMapSerializerCalled_EmitsMassTransitEnvelopeContentType()
    {
        // Arrange — publish-only bridge: OrderCreated → MassTransitEnvelopeSerializer, no receive endpoint
        var (sp, captured) = BuildServiceProvider(bus =>
        {
            bus.MapSerializer<BridgeOrderCreated, MassTransitEnvelopeSerializer>();
        });

        IBus bus = sp.GetRequiredService<IBus>();

        // Start the bus (BareWireBusControl starts the background publish loop)
        IBusControl control = sp.GetRequiredService<IBusControl>();
        await control.StartAsync(CancellationToken.None);

        // Act
        await bus.PublishAsync(new BridgeOrderCreated("ORD-1"), CancellationToken.None);
        await Task.Delay(80); // allow background publish loop to flush

        // Assert — message published with MassTransit envelope content-type
        captured.Should().ContainSingle();
        captured[0].ContentType.Should().Be("application/vnd.masstransit+json");

        await control.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PublishOnlyBridge_WhenMapSerializerCalled_OtherTypesStillUseDefault()
    {
        // Arrange — only BridgeOrderCreated is mapped; BridgePaymentRequested stays raw JSON
        var (sp, captured) = BuildServiceProvider(bus =>
        {
            bus.MapSerializer<BridgeOrderCreated, MassTransitEnvelopeSerializer>();
        });

        IBus bus = sp.GetRequiredService<IBus>();
        IBusControl control = sp.GetRequiredService<IBusControl>();
        await control.StartAsync(CancellationToken.None);

        // Act — publish both types
        await bus.PublishAsync(new BridgeOrderCreated("ORD-2"), CancellationToken.None);
        await bus.PublishAsync(new BridgePaymentRequested("PAY-1"), CancellationToken.None);
        await Task.Delay(80);

        // Assert — two messages captured; first is envelope, second is raw JSON
        captured.Should().HaveCount(2);

        OutboundMessage? orderMsg = captured.FirstOrDefault(m => m.ContentType == "application/vnd.masstransit+json");
        OutboundMessage? paymentMsg = captured.FirstOrDefault(m => m.ContentType == "application/json");

        orderMsg.Should().NotBeNull("OrderCreated should be serialized with MassTransit envelope");
        paymentMsg.Should().NotBeNull("PaymentRequested should use the default raw JSON serializer");

        await control.StopAsync(CancellationToken.None);
    }

    // ── Message types ──────────────────────────────────────────────────────────

    public sealed record BridgeOrderCreated(string OrderId);
    public sealed record BridgePaymentRequested(string PaymentId);
}
