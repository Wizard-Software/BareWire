using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Pipeline;
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
/// Unit tests for <see cref="BusShutdownOptions"/> and for the graceful-drain coordination that
/// <see cref="BareWireBusControl.StopAsync"/> performs before cancelling consumer loops, when the
/// transport adapter implements the internal <see cref="IGracefulDrainTransport"/> seam.
/// </summary>
public sealed class BareWireBusControlGracefulDrainTests
{
    private const string EndpointName = "drain-queue";

    private sealed record DrainProbe(int Value);
    private sealed record FollowUp(int Value);

    private sealed class DrainProbeConsumer : IConsumer<DrainProbe>
    {
        public Task ConsumeAsync(ConsumeContext<DrainProbe> context) => Task.CompletedTask;
    }

    /// <summary>
    /// Bundles the control under test with the test doubles a graceful-drain scenario needs to
    /// observe: the transport adapter, the events log (drain / consumer-cancellation ordering),
    /// and the consumer loop's own cancellation token (captured from the first
    /// <see cref="ITransportAdapter.ConsumeAsync"/> call).
    /// </summary>
    private sealed class Harness
    {
        internal BareWireBusControl Control { get; set; } = null!;
        internal ITransportAdapter Adapter { get; set; } = null!;
        internal ConcurrentQueue<string> Events { get; } = new();
        internal TaskCompletionSource<CancellationToken> ConsumerTokenReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Never actually reaches a message — the consume stream blocks until the runner's token is
    // cancelled, at which point it records "consumers-cancelled" so tests can assert ordering.
    private static async IAsyncEnumerable<InboundMessage> ConsumeForeverAsync(
        Harness harness, [EnumeratorCancellation] CancellationToken consumerToken)
    {
        harness.ConsumerTokenReady.TrySetResult(consumerToken);

        try
        {
            await Task.Delay(Timeout.Infinite, consumerToken).ConfigureAwait(false);
        }
        finally
        {
            harness.Events.Enqueue("consumers-cancelled");
        }

        yield break;
    }

    private static Harness CreateControl(BusShutdownOptions? shutdownOptions, bool withDrainSeam = true)
    {
        ITransportAdapter adapter = withDrainSeam
            ? Substitute.For<ITransportAdapter, IGracefulDrainTransport>()
            : Substitute.For<ITransportAdapter>();

        adapter.TransportName.Returns("test");
        adapter.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);
        adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });

        Harness harness = new() { Adapter = adapter };

        adapter.ConsumeAsync(Arg.Any<string>(), Arg.Any<FlowControlOptions>(), Arg.Any<CancellationToken>())
               .Returns(callInfo => ConsumeForeverAsync(harness, callInfo.ArgAt<CancellationToken>(2)));

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer.When(s => s.Serialize(Arg.Any<object>(), Arg.Any<IBufferWriter<byte>>())).Do(_ => { });

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IEnumerable<IMessageMiddleware>)).Returns(Array.Empty<IMessageMiddleware>());

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

        EndpointBinding binding = new()
        {
            EndpointName = EndpointName,
            PrefetchCount = 4,
            Consumers = [new ConsumerRegistration(typeof(DrainProbeConsumer), typeof(DrainProbe))],
        };

        BareWireBusControl control = new(
            bus,
            adapter,
            flowController,
            configurator,
            NullLogger<BareWireBusControl>.Instance,
            topology: null,
            endpointBindings: [binding],
            deserializerResolver: deserializerResolver,
            scopeFactory: scopeFactory,
            instrumentation: new NullInstrumentation(),
            loggerFactory: NullLoggerFactory.Instance,
            sagaDispatchers: [],
            shutdownOptions: shutdownOptions);

        harness.Control = control;
        return harness;
    }

    // ── BusShutdownOptions ────────────────────────────────────────────────────

    [Fact]
    public void BusShutdownOptions_Default_DrainTimeoutIsTenSeconds()
        => new BusShutdownOptions().DrainTimeout.Should().Be(TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BusShutdownOptions_DrainTimeoutNotPositive_ThrowsArgumentOutOfRange(int ms)
    {
        Action act = () => _ = new BusShutdownOptions { DrainTimeout = TimeSpan.FromMilliseconds(ms) };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BusShutdownOptions_DrainTimeoutExceedsTimerBound_ThrowsArgumentOutOfRange()
    {
        Action act = () => _ = new BusShutdownOptions { DrainTimeout = TimeSpan.MaxValue };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── StopAsync graceful drain ──────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_WithDrainTransport_DrainsBeforeCancellingConsumersThenDisposesBus()
    {
        Harness h = CreateControl(shutdownOptions: null);
        ((IGracefulDrainTransport)h.Adapter).DrainAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                h.Events.Enqueue("drain");
                return Task.CompletedTask;
            });

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.Control.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        h.Events.Should().ContainInOrder("drain", "consumers-cancelled");

        Func<Task> publish = () => h.Control.PublishAsync(new DrainProbe(1));
        await publish.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task StopAsync_FollowUpEventStillInPublishChannelAfterAck_IsConsumedBeforeConsumersAreCancelled()
    {
        Harness h = CreateControl(shutdownOptions: null);

        int inFlight = 1; // a message is currently "being handled" when the drain starts
        int handlerStarted = 0;
        bool followUpConsumed = false;
        bool consumerTokenCancelledWhenFollowUpConsumed = true; // poisoned — must be overwritten below

        ((IGracefulDrainTransport)h.Adapter).DrainAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                CancellationToken token = callInfo.ArgAt<CancellationToken>(1);

                if (Interlocked.Exchange(ref handlerStarted, 1) == 0)
                {
                    // Simulated in-flight handler: publishes a follow-up event, then Acks.
                    _ = Task.Run(async () =>
                    {
                        await h.Control.PublishAsync(new FollowUp(1));
                        Interlocked.Decrement(ref inFlight);
                    });
                }

                while (Volatile.Read(ref inFlight) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(1, CancellationToken.None).ConfigureAwait(false);
                }
            });

        h.Adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);

                // Wait until the original message was Acked before "accepting" the follow-up.
                while (Volatile.Read(ref inFlight) != 0)
                    await Task.Delay(1, CancellationToken.None).ConfigureAwait(false);

                Interlocked.Increment(ref inFlight);

                _ = Task.Run(async () =>
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    CancellationToken consumerToken = await h.ConsumerTokenReady.Task.ConfigureAwait(false);
                    consumerTokenCancelledWhenFollowUpConsumed = consumerToken.IsCancellationRequested;
                    followUpConsumed = true;
                    Interlocked.Decrement(ref inFlight);
                });

                return (IReadOnlyList<SendResult>)messages.Select(static _ => new SendResult(true, 0UL)).ToList();
            });

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.Control.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        followUpConsumed.Should().BeTrue();
        consumerTokenCancelledWhenFollowUpConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenMessageAcceptedByTransportBetweenChecks_WaitsForItBeforeCancellingConsumers()
    {
        Harness h = CreateControl(shutdownOptions: null);

        int inFlight = 0;
        int handlerStarted = 0;
        bool followUpConsumed = false;
        bool consumerTokenCancelledWhenFollowUpConsumed = true; // poisoned — must be overwritten below
        TaskCompletionSource followUpAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ((IGracefulDrainTransport)h.Adapter).DrainAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                TimeSpan timeout = callInfo.ArgAt<TimeSpan>(0);
                CancellationToken token = callInfo.ArgAt<CancellationToken>(1);

                if (Interlocked.Exchange(ref handlerStarted, 1) == 0)
                {
                    // First call: the transport's own counters are already at zero, but a follow-up
                    // message is published and fully accepted by the transport before this call
                    // returns. The settle delay afterwards lets the publish loop go idle again, so
                    // the outer check right after this call sees it as idle — exactly as if the
                    // acceptance had raced the snapshot taken there.
                    _ = Task.Run(() => h.Control.PublishAsync(new FollowUp(1)));
                    await followUpAccepted.Task.ConfigureAwait(false);
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // Every later call honours the real drain contract: poll for the accepted message to
                // clear, the timeout argument to elapse, or the token to be cancelled.
                Stopwatch watch = Stopwatch.StartNew();
                while (Volatile.Read(ref inFlight) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.Elapsed >= timeout)
                        return;

                    await Task.Delay(1, CancellationToken.None).ConfigureAwait(false);
                }
            });

        h.Adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);

                Interlocked.Increment(ref inFlight);
                followUpAccepted.TrySetResult();

                _ = Task.Run(async () =>
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    CancellationToken consumerToken = await h.ConsumerTokenReady.Task.ConfigureAwait(false);
                    consumerTokenCancelledWhenFollowUpConsumed = consumerToken.IsCancellationRequested;
                    followUpConsumed = true;
                    Interlocked.Decrement(ref inFlight);
                });

                return Task.FromResult<IReadOnlyList<SendResult>>(
                    messages.Select(static _ => new SendResult(true, 0UL)).ToList());
            });

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.Control.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        followUpConsumed.Should().BeTrue();
        consumerTokenCancelledWhenFollowUpConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_WithBatchInFlightInPublishLoop_DoesNotCompleteDrainPrematurely()
    {
        Harness h = CreateControl(shutdownOptions: null);
        ((IGracefulDrainTransport)h.Adapter).DrainAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        TaskCompletionSource sendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                sendEntered.TrySetResult();
                await releaseSend.Task.ConfigureAwait(false);
                return (IReadOnlyList<SendResult>)messages.Select(static _ => new SendResult(true, 0UL)).ToList();
            });

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.Control.PublishAsync(new DrainProbe(1));
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task stop = h.Control.StopAsync();
        await Task.Delay(200);

        stop.IsCompleted.Should().BeFalse();
        CancellationToken consumerToken = await h.ConsumerTokenReady.Task;
        consumerToken.IsCancellationRequested.Should().BeFalse();

        releaseSend.SetResult();

        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        consumerToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_WhenDrainNeverCompletes_CancelsConsumersAfterDrainTimeout()
    {
        BusShutdownOptions options = new() { DrainTimeout = TimeSpan.FromMilliseconds(300) };
        Harness h = CreateControl(options);

        List<TimeSpan> receivedTimeouts = [];
        ((IGracefulDrainTransport)h.Adapter).DrainAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (receivedTimeouts)
                    receivedTimeouts.Add(callInfo.ArgAt<TimeSpan>(0));

                // Ignores the timeout argument on purpose — the core must still enforce the limit
                // through the cancellation token it passes alongside it.
                return Task.Delay(Timeout.Infinite, callInfo.ArgAt<CancellationToken>(1));
            });

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Stopwatch sw = Stopwatch.StartNew();
        await h.Control.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
        CancellationToken consumerToken = await h.ConsumerTokenReady.Task;
        consumerToken.IsCancellationRequested.Should().BeTrue();
        receivedTimeouts.Should().OnlyContain(t => t <= TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task StopAsync_WhenDrainIgnoresTimeoutAndToken_CancelsConsumersAfterDrainTimeout()
    {
        BusShutdownOptions options = new() { DrainTimeout = TimeSpan.FromMilliseconds(300) };
        Harness h = CreateControl(options);

        // A non-cooperative adapter: the returned task never completes, whatever timeout or token it is given.
        TaskCompletionSource neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IGracefulDrainTransport)h.Adapter).DrainAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(neverCompletes.Task);

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Stopwatch sw = Stopwatch.StartNew();
        await h.Control.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
        CancellationToken consumerToken = await h.ConsumerTokenReady.Task;
        consumerToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_WhenHostTokenCancelledDuringDrain_StopsDrainingAndCancelsConsumers()
    {
        BusShutdownOptions options = new() { DrainTimeout = TimeSpan.FromSeconds(30) };
        Harness h = CreateControl(options);

        ((IGracefulDrainTransport)h.Adapter).DrainAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.Delay(Timeout.Infinite, callInfo.ArgAt<CancellationToken>(1)));

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using CancellationTokenSource host = new(TimeSpan.FromMilliseconds(200));
        await h.Control.StopAsync(host.Token).WaitAsync(TimeSpan.FromSeconds(5));

        CancellationToken consumerToken = await h.ConsumerTokenReady.Task;
        consumerToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_WhenDrainThrows_ContinuesShutdown()
    {
        Harness h = CreateControl(shutdownOptions: null);
        ((IGracefulDrainTransport)h.Adapter).DrainAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("transport drain failed"));

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.Control.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        CancellationToken consumerToken = await h.ConsumerTokenReady.Task;
        consumerToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_WithoutDrainSeam_CancelsConsumersWithoutWaitingForPublishLoop()
    {
        Harness h = CreateControl(shutdownOptions: null, withDrainSeam: false);

        TaskCompletionSource sendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                sendEntered.TrySetResult();

                // Deliberately depends on consumer cancellation — proves the regression: without the
                // drain seam, StopAsync must not wait for publish-loop quiescence before cancelling
                // consumers, or this would deadlock forever.
                CancellationToken consumerToken = await h.ConsumerTokenReady.Task.ConfigureAwait(false);
                while (!consumerToken.IsCancellationRequested)
                    await Task.Delay(5).ConfigureAwait(false);

                return (IReadOnlyList<SendResult>)messages.Select(static _ => new SendResult(true, 0UL)).ToList();
            });

        await h.Control.StartAsync();
        await h.ConsumerTokenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.Control.PublishAsync(new DrainProbe(1));
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.Control.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        CancellationToken consumerTokenFinal = await h.ConsumerTokenReady.Task;
        consumerTokenFinal.IsCancellationRequested.Should().BeTrue();
    }
}
