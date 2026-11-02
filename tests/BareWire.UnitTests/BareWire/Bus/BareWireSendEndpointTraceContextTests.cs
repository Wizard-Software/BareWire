using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Transport;
using BareWire.Configuration;
using BareWire.Interop.MassTransit;
using BareWire.Observability;
using BareWire.Serialization.Json;
using BareWire.UnitTests.Observability;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Verifies that <c>ISendEndpoint.SendAsync</c>, <c>SendRawAsync</c> and <c>IPublishEndpoint.PublishRawAsync</c>
/// propagate the W3C trace context, and that their span tags never carry credentials or correlation values.
/// </summary>
[Collection(ActivityListenerFixtureGroup.Name)]
public sealed class BareWireSendEndpointTraceContextTests : IDisposable
{
    private readonly List<Activity> _stopped = [];
    private ActivityListener? _listener;

    public void Dispose() => _listener?.Dispose();

    private void StartListening()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "BareWire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_stopped)
                {
                    _stopped.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public async Task SendAsync_WithActiveListener_InjectsTraceparent()
    {
        // Arrange
        StartListening();
        await using var harness = await StartBusAsync();
        ISendEndpoint endpoint = await harness.Bus.GetSendEndpoint(new Uri("queue:orders"), TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendAsync(new SendTraceEvent("x"), TestContext.Current.CancellationToken);
        OutboundMessage message = await harness.WaitForSingleAsync();

        // Assert
        message.Headers.Should().ContainKey("traceparent");
        message.Headers.Should().ContainKey("BW-Exchange");
    }

    [Fact]
    public async Task SendAsync_ToPlainAddressWithActiveListener_InjectsTraceparent()
    {
        // Arrange — a scheme that is neither queue: nor exchange: used to produce no headers at all.
        StartListening();
        await using var harness = await StartBusAsync();
        ISendEndpoint endpoint = await harness.Bus.GetSendEndpoint(new Uri("amqp://localhost/orders"), TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendAsync(new SendTraceEvent("x"), TestContext.Current.CancellationToken);
        OutboundMessage message = await harness.WaitForSingleAsync();

        // Assert
        message.Headers.Should().ContainKey("traceparent");
    }

    [Fact]
    public async Task SendAsync_WithoutListener_EmitsNoTraceHeaders()
    {
        // Arrange — observability registered, but nobody listens to the activity source.
        await using var harness = await StartBusAsync();
        ISendEndpoint endpoint = await harness.Bus.GetSendEndpoint(new Uri("amqp://localhost/orders"), TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendAsync(new SendTraceEvent("x"), TestContext.Current.CancellationToken);
        OutboundMessage message = await harness.WaitForSingleAsync();

        // Assert
        message.Headers.Should().NotContainKey("traceparent");
        message.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_WithMassTransitSerializerAndNoListener_EmitsNoTraceHeaders()
    {
        // Arrange — guard: the MT contributor must not add anything when there is no trace to copy.
        await using var harness = await StartBusAsync(
            bus => bus.MapSerializer<SendTraceEvent, MassTransitEnvelopeSerializer>());
        ISendEndpoint endpoint = await harness.Bus.GetSendEndpoint(new Uri("amqp://localhost/orders"), TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendAsync(new SendTraceEvent("x"), TestContext.Current.CancellationToken);
        OutboundMessage message = await harness.WaitForSingleAsync();

        // Assert
        message.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_WithNullInstrumentation_EmitsNoTraceHeadersAndDoesNotThrow()
    {
        // Arrange — AddBareWireObservability() is not called, so the core no-op instrumentation is used.
        StartListening();
        await using var harness = await StartBusAsync(withObservability: false);
        ISendEndpoint endpoint = await harness.Bus.GetSendEndpoint(new Uri("queue:orders"), TestContext.Current.CancellationToken);

        // Act
        Func<Task> act = () => endpoint.SendAsync(new SendTraceEvent("x"), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync();
        OutboundMessage message = await harness.WaitForSingleAsync();
        message.Headers.Should().NotContainKey("traceparent");
    }

    [Fact]
    public async Task SendAsync_WithAddressContainingUserInfoAndQuery_SpanTagsExcludeSecretsAndCorrelationId()
    {
        // Arrange
        StartListening();
        await using var harness = await StartBusAsync();
        ISendEndpoint endpoint = await harness.Bus.GetSendEndpoint(
            new Uri("queue://user:s3cr3tpw@localhost/orders?correlation-id=corr-123"),
            TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendAsync(new SendTraceEvent("x"), TestContext.Current.CancellationToken);
        await harness.WaitForSingleAsync();

        // Assert
        Activity span = PublishSpans().Should().ContainSingle().Subject;
        string tags = string.Join('|', span.TagObjects.Select(static t => $"{t.Key}={t.Value}"));
        tags.Should().NotContain("s3cr3tpw").And.NotContain("user").And.NotContain("corr-123");
        span.GetTagItem("messaging.destination").Should().Be("queue://localhost/orders");
    }

    [Fact]
    public async Task SendRawAsync_WithActiveListener_InjectsTraceparent()
    {
        // Arrange
        StartListening();
        await using var harness = await StartBusAsync();
        ISendEndpoint endpoint = await harness.Bus.GetSendEndpoint(new Uri("queue:orders"), TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendRawAsync(new byte[] { 1, 2, 3 }, "application/octet-stream", TestContext.Current.CancellationToken);
        OutboundMessage message = await harness.WaitForSingleAsync();

        // Assert
        message.Headers.Should().ContainKey("traceparent");
        message.Headers.Should().ContainKey("BW-Exchange");
        message.Headers.Should().NotContainKey("MT-Activity-Id");
    }

    [Fact]
    public async Task SendRawAsync_WithoutListener_EmitsNoTraceHeaders()
    {
        // Arrange
        await using var harness = await StartBusAsync();
        ISendEndpoint endpoint = await harness.Bus.GetSendEndpoint(new Uri("amqp://localhost/orders"), TestContext.Current.CancellationToken);

        // Act
        await endpoint.SendRawAsync(new byte[] { 1 }, "application/octet-stream", TestContext.Current.CancellationToken);
        OutboundMessage message = await harness.WaitForSingleAsync();

        // Assert
        message.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishRawAsync_WithActiveListener_InjectsTraceparent()
    {
        // Arrange
        StartListening();
        await using var harness = await StartBusAsync();

        // Act
        await harness.Bus.PublishRawAsync(new byte[] { 1, 2 }, "application/octet-stream", TestContext.Current.CancellationToken);
        OutboundMessage message = await harness.WaitForSingleAsync();

        // Assert
        message.Headers.Should().ContainKey("traceparent");
    }

    [Fact]
    public async Task PublishRawAsync_WithoutListener_EmitsNoTraceHeaders()
    {
        // Arrange
        await using var harness = await StartBusAsync();

        // Act
        await harness.Bus.PublishRawAsync(new byte[] { 1, 2 }, "application/octet-stream", TestContext.Current.CancellationToken);
        OutboundMessage message = await harness.WaitForSingleAsync();

        // Assert
        message.Headers.Should().BeEmpty();
    }

    private List<Activity> PublishSpans()
    {
        lock (_stopped)
        {
            return _stopped.Where(static a => a.Kind == ActivityKind.Producer).ToList();
        }
    }

    internal static async Task<BusHarness> StartBusAsync(
        Action<IBusConfigurator>? configureBus = null,
        bool withObservability = true)
    {
        ServiceCollection services = new();
        services.AddBareWireJsonSerializer();
        services.AddMassTransitEnvelopeSerializer();

        List<OutboundMessage> captured = [];
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
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

        services.AddBareWire(configureBus ?? (static _ => { }));
        ServiceDescriptor? configuratorDescriptor = services
            .FirstOrDefault(d => d.ServiceType == typeof(BusConfigurator));
        if (configuratorDescriptor?.ImplementationInstance is BusConfigurator busConfigurator)
        {
            busConfigurator.HasInMemoryTransport = true;
        }

        services.AddLogging();
        if (withObservability)
        {
            services.AddSingleton<System.Diagnostics.Metrics.IMeterFactory, TestMeterFactory>();
            services.AddBareWireObservability(static o => o.EnableOpenTelemetry = false);
        }

        ServiceProvider sp = services.BuildServiceProvider();
        IBusControl control = sp.GetRequiredService<IBusControl>();
        await control.StartAsync(TestContext.Current.CancellationToken);
        return new BusHarness(sp, control, captured);
    }

    internal sealed class BusHarness(ServiceProvider services, IBusControl control, List<OutboundMessage> captured)
        : IAsyncDisposable
    {
        public IBus Bus => services.GetRequiredService<IBus>();

        public async Task<OutboundMessage> WaitForSingleAsync()
        {
            for (int i = 0; i < 200; i++)
            {
                lock (captured)
                {
                    if (captured.Count > 0)
                    {
                        return captured[0];
                    }
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException("No outbound message was captured.");
        }

        public async ValueTask DisposeAsync()
        {
            await control.StopAsync(CancellationToken.None);
            await services.DisposeAsync();
        }
    }

    public sealed record SendTraceEvent(string Id);
}
