using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.InMemory;
using BareWire.Observability;
using BareWire.Serialization.Json;
using BareWire.UnitTests.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Verifies end-to-end trace-context propagation on the in-memory transport: the request client injects the
/// trace context, and the consume span is the parent of the work done inside the consumer.
/// </summary>
[Collection(ActivityListenerFixtureGroup.Name)]
public sealed class InMemoryTraceContextTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener? _listener;

    public void Dispose() => _listener?.Dispose();

    private void StartListening()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "BareWire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private static async Task<(ServiceProvider Provider, IBus Bus, TraceProbe Probe)> StartBusAsync()
    {
        var probe = new TraceProbe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMeterFactory, TestMeterFactory>();
        services.AddBareWireObservability(static o => o.EnableOpenTelemetry = false);
        services.AddBareWireJsonSerializer();
        services.AddSingleton(probe);
        services.AddTransient<TracedPingConsumer>();
        services.AddTransient<TracedEventConsumer>();
        services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange(string.Empty);
            t.AutoDeclareEndpointQueues();
            t.MapRoutingKey<TracedPing>("traced-ping");
            t.MapRoutingKey<TracedEvent>("traced-event");
            t.ReceiveEndpoint("traced-ping", e => e.Consumer<TracedPingConsumer, TracedPing>());
            t.ReceiveEndpoint("traced-event", e => e.Consumer<TracedEventConsumer, TracedEvent>());
        });

        ServiceProvider provider = services.BuildServiceProvider();
        var control = provider.GetRequiredService<IBusControl>();
        await control.StartAsync(TestContext.Current.CancellationToken);
        return (provider, (IBus)control, probe);
    }

    [Fact]
    public async Task GetResponseAsync_WithActiveListener_SendsTraceparentAndConsumerJoinsRequestTrace()
    {
        // Arrange
        StartListening();
        (ServiceProvider provider, IBus bus, TraceProbe probe) = await StartBusAsync();
        await using (provider)
        {
            await using IRequestClient<TracedPing> client =
                await bus.CreateRequestClientAsync<TracedPing>(TestContext.Current.CancellationToken);

            // Act
            _ = await client.GetResponseAsync<TracedPong>(new TracedPing("a"), TestContext.Current.CancellationToken);

            // Assert
            probe.Headers.Should().ContainKey("traceparent");
            Activity requestSpan = _stopped.Should()
                .ContainSingle(a => a.Kind == ActivityKind.Producer && a.DisplayName == "TracedPing publish").Subject;
            probe.CurrentTraceId.Should().Be(requestSpan.TraceId);
        }
    }

    [Fact]
    public async Task GetResponseAsync_WithoutListener_SendsNoTraceparent()
    {
        // Arrange
        (ServiceProvider provider, IBus bus, TraceProbe probe) = await StartBusAsync();
        await using (provider)
        {
            await using IRequestClient<TracedPing> client =
                await bus.CreateRequestClientAsync<TracedPing>(TestContext.Current.CancellationToken);

            // Act
            _ = await client.GetResponseAsync<TracedPong>(new TracedPing("a"), TestContext.Current.CancellationToken);

            // Assert
            probe.Headers.Should().NotContainKey("traceparent");
        }
    }

    [Fact]
    public async Task PublishAsync_WithActiveListener_ConsumeSpanIsCurrentInsideConsumer()
    {
        // Arrange
        StartListening();
        (ServiceProvider provider, IBus bus, TraceProbe probe) = await StartBusAsync();
        await using (provider)
        {
            // Act
            await bus.PublishAsync(new TracedEvent("e"), TestContext.Current.CancellationToken);
            await probe.Consumed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await WaitForStoppedSpanAsync("TracedEvent process");

            // Assert
            Activity publishSpan = _stopped.Should()
                .ContainSingle(a => a.DisplayName == "TracedEvent publish").Subject;
            Activity consumeSpan = _stopped.Should()
                .ContainSingle(a => a.DisplayName == "TracedEvent process").Subject;
            consumeSpan.Kind.Should().Be(ActivityKind.Consumer);
            consumeSpan.ParentSpanId.Should().Be(publishSpan.SpanId);
            probe.CurrentSpanId.Should().Be(consumeSpan.SpanId);
        }
    }

    // The consume span stops after the consumer returns, so the test polls for it instead of sleeping.
    private async Task WaitForStoppedSpanAsync(string displayName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!_stopped.Any(a => a.DisplayName == displayName))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public sealed record TracedPing(string Text);

    public sealed record TracedPong(string Text);

    public sealed record TracedEvent(string Text);

    internal sealed class TraceProbe
    {
        public IReadOnlyDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        public ActivityTraceId CurrentTraceId { get; set; }

        public ActivitySpanId CurrentSpanId { get; set; }

        public TaskCompletionSource Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Capture(IReadOnlyDictionary<string, string> headers)
        {
            Headers = new Dictionary<string, string>(headers);
            CurrentTraceId = Activity.Current?.TraceId ?? default;
            CurrentSpanId = Activity.Current?.SpanId ?? default;
        }
    }

    internal sealed class TracedPingConsumer(TraceProbe probe) : IConsumer<TracedPing>
    {
        public Task ConsumeAsync(ConsumeContext<TracedPing> context)
        {
            probe.Capture(context.Headers);
            return context.RespondAsync(new TracedPong("pong:" + context.Message.Text));
        }
    }

    internal sealed class TracedEventConsumer(TraceProbe probe) : IConsumer<TracedEvent>
    {
        public Task ConsumeAsync(ConsumeContext<TracedEvent> context)
        {
            probe.Capture(context.Headers);
            probe.Consumed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
