using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Transport;
using BareWire.Configuration;
using BareWire.Interop.MassTransit;
using BareWire.Observability;
using BareWire.Serialization.Json;
using BareWire.Abstractions.Serialization;
using BareWire.UnitTests.Abstractions;
using BareWire.UnitTests.Observability;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.Interop;

/// <summary>
/// Verifies that trace context crosses the BareWire to MassTransit boundary: the MassTransit envelope
/// serializer must additionally emit <c>MT-Activity-Id</c>, while the raw-first default must not.
/// </summary>
[Collection(ActivityListenerFixtureGroup.Name)]
public sealed class MassTransitTraceContextPropagationTests : IDisposable
{
    private readonly ActivityListener _listener;

    public MassTransitTraceContextPropagationTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "BareWire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task PublishAsync_WithMassTransitEnvelopeAndActiveListener_EmitsMtActivityIdEqualToTraceparent()
    {
        // Arrange
        var (sp, captured) = BuildServiceProvider(bus => bus.MapSerializer<BridgeTraceEvent, MassTransitEnvelopeSerializer>());
        IBusControl control = sp.GetRequiredService<IBusControl>();
        await control.StartAsync(TestContext.Current.CancellationToken);

        // Act
        await sp.GetRequiredService<IBus>().PublishAsync(new BridgeTraceEvent("x"), TestContext.Current.CancellationToken);
        OutboundMessage message = await WaitForSingleAsync(captured);
        await control.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        message.Headers.Should().ContainKey("traceparent");
        message.Headers.Should().ContainKey("MT-Activity-Id");
        message.Headers["MT-Activity-Id"].Should().Be(message.Headers["traceparent"]);
    }

    [Fact]
    public async Task PublishAsync_WithDefaultRawSerializer_DoesNotEmitMtActivityId()
    {
        // Arrange — no MapSerializer: the raw-first default serializer is used.
        var (sp, captured) = BuildServiceProvider(static _ => { });
        IBusControl control = sp.GetRequiredService<IBusControl>();
        await control.StartAsync(TestContext.Current.CancellationToken);

        // Act
        await sp.GetRequiredService<IBus>().PublishAsync(new BridgeTraceEvent("x"), TestContext.Current.CancellationToken);
        OutboundMessage message = await WaitForSingleAsync(captured);
        await control.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        message.Headers.Should().ContainKey("traceparent");
        message.Headers.Should().NotContainKey("MT-Activity-Id");
    }

    [Fact]
    public async Task SendAsync_WithMassTransitEnvelopeAndActiveListener_EmitsTraceparentAndMtActivityId()
    {
        // Arrange
        var (sp, captured) = BuildServiceProvider(bus => bus.MapSerializer<BridgeTraceEvent, MassTransitEnvelopeSerializer>());
        IBusControl control = sp.GetRequiredService<IBusControl>();
        await control.StartAsync(TestContext.Current.CancellationToken);
        ISendEndpoint endpoint = await sp.GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri("queue:orders"), TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendAsync(new BridgeTraceEvent("x"), TestContext.Current.CancellationToken);
        OutboundMessage message = await WaitForSingleAsync(captured);
        await control.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        message.Headers.Should().ContainKey("traceparent");
        message.Headers.Should().ContainKey("MT-Activity-Id");
        message.Headers["MT-Activity-Id"].Should().Be(message.Headers["traceparent"]);
    }

    [Fact]
    public async Task RespondAsync_OnMassTransitRequest_EmitsMtActivityIdEqualToTraceparent()
    {
        // Arrange — a BareWire consumer answering a MassTransit request: the reply goes through the raw send path
        // and the response envelope writer is the header contributor.
        var (sp, captured) = BuildServiceProvider(static _ => { });
        IBusControl control = sp.GetRequiredService<IBusControl>();
        await control.StartAsync(TestContext.Current.CancellationToken);
        var context = new RespondTestableConsumeContext(
            Guid.NewGuid(),
            correlationId: null,
            conversationId: null,
            sourceAddress: null,
            destinationAddress: null,
            sentTime: null,
            headers: new Dictionary<string, string>(),
            contentType: "application/vnd.masstransit+json",
            rawBody: default,
            publishEndpoint: sp.GetRequiredService<IBus>(),
            sendEndpointProvider: sp.GetRequiredService<IBus>(),
            cancellationToken: TestContext.Current.CancellationToken)
        {
            InboundRequestContext = new RequestEnvelopeContext(
                ResponseAddress: "rabbitmq://localhost/mt-reply-queue",
                DestinationAddress: null,
                FaultAddress: null,
                RequestId: Guid.NewGuid(),
                CorrelationId: null,
                ExpirationTime: null),
            ResponseEnvelopeWriter = new MassTransitEnvelopeSerializer(),
        };

        // Act
        await context.RespondAsync(new BridgeTraceEvent("reply"), TestContext.Current.CancellationToken);
        OutboundMessage message = await WaitForSingleAsync(captured);
        await control.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        message.Headers.Should().ContainKey("traceparent");
        message.Headers["MT-Activity-Id"].Should().Be(message.Headers["traceparent"]);
    }

    [Fact]
    public async Task SendRawAsync_WithoutResponseContributor_DoesNotEmitMtActivityId()
    {
        // Arrange — raw-first: a plain raw send never detects MassTransit, so it carries traceparent only.
        var (sp, captured) = BuildServiceProvider(static _ => { });
        IBusControl control = sp.GetRequiredService<IBusControl>();
        await control.StartAsync(TestContext.Current.CancellationToken);
        ISendEndpoint endpoint = await sp.GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri("queue:orders"), TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendRawAsync(
            new byte[] { 1 }, "application/vnd.masstransit+json", TestContext.Current.CancellationToken);
        OutboundMessage message = await WaitForSingleAsync(captured);
        await control.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        message.Headers.Should().ContainKey("traceparent");
        message.Headers.Should().NotContainKey("MT-Activity-Id");
    }

    private static async Task<OutboundMessage> WaitForSingleAsync(List<OutboundMessage> captured)
    {
        for (int i = 0; i < 100; i++)
        {
            lock (captured)
            {
                if (captured.Count > 0)
                {
                    return captured[0];
                }
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("No outbound message was captured.");
    }

    private static (IServiceProvider Services, List<OutboundMessage> Captured) BuildServiceProvider(
        Action<IBusConfigurator> configureBus)
    {
        ServiceCollection services = new();

        services.AddBareWireJsonSerializer();
        services.AddMassTransitEnvelopeSerializer();

        List<OutboundMessage> captured = [];
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                lock (captured)
                {
                    captured.AddRange(messages);
                }

                return Task.FromResult<IReadOnlyList<SendResult>>(
                    messages.Select(static _ => new SendResult(true, 0UL)).ToList());
            });
        services.AddSingleton(adapter);

        services.AddBareWire(configureBus);

        ServiceDescriptor? configuratorDescriptor = services
            .FirstOrDefault(d => d.ServiceType == typeof(BusConfigurator));
        if (configuratorDescriptor?.ImplementationInstance is BusConfigurator busConfigurator)
        {
            busConfigurator.HasInMemoryTransport = true;
        }

        services.AddLogging();
        services.AddSingleton<System.Diagnostics.Metrics.IMeterFactory, TestMeterFactory>();
        services.AddBareWireObservability(static o => o.EnableOpenTelemetry = false);

        return (services.BuildServiceProvider(), captured);
    }

    public sealed record BridgeTraceEvent(string Id);
}
