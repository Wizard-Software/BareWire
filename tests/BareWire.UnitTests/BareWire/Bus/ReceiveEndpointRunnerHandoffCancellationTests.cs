using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Regression guard for the window between "message read from the transport" and "message handed to its
/// owner" in <c>ReceiveEndpointRunner.RunAsync</c>. When the runner's token is cancelled while the read
/// loop waits for credit or for a free lane slot, the message it already holds must be requeued, its
/// pooled buffer returned and (if it was granted) its inflight credit released.
/// </summary>
public sealed class ReceiveEndpointRunnerHandoffCancellationTests
{
    private const string QueueName = "handoff-cancellation";
    private const int MessageLength = 16;
    private static readonly TimeSpan s_waitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunAsync_CancelledWhileWaitingForLaneSlot_DisposesMessageAndReleasesCredit()
    {
        // Arrange — 2 lanes, PrefetchCount 4 -> lane depth 2; one hot key -> one lane.
        // m-0 is held by the lane worker (stalled consumer), m-1/m-2 fill the lane channel,
        // m-3 gets credit and blocks on the lane channel write.
        var adapter = new HandoffFakeAdapter(messageCount: 4);
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: 2);
        (ReceiveEndpointRunner runner, CreditManager credit, StallSignal signal) = CreateRunner(binding, adapter);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task run = runner.RunAsync(cts.Token);
        await signal.Entered.Task.WaitAsync(s_waitLimit, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => credit.InflightCount == 4, s_waitLimit); // m-3 holds a credit
        await cts.CancelAsync();
        await run.WaitAsync(s_waitLimit, TestContext.Current.CancellationToken);

        // Assert
        adapter.Yielded.Should().HaveCount(4);
        adapter.Yielded.Should().AllSatisfy(m => IsDisposed(m).Should().BeTrue(
            "every message read by the runner must have its pooled buffer returned"));
        credit.InflightCount.Should().Be(0);
        credit.InflightBytes.Should().Be(0);

        adapter.Settlements.Where(s => s.MessageId == "m-3").Should().ContainSingle(
                "the message abandoned in the handoff window is settled exactly once")
            .Which.Action.Should().Be(SettlementAction.Requeue);

        foreach (string id in new[] { "m-0", "m-1", "m-2" })
        {
            adapter.Settlements.Count(s => s.MessageId == id).Should().BeLessThanOrEqualTo(1,
                $"{id} must not be settled more than once");
        }
    }

    [Theory]
    [InlineData(false)] // sequential path (defensive window: credit held outside the loop)
    [InlineData(true)] // ordered path
    public async Task RunAsync_CancelledWhileWaitingForCredit_RequeuesAndDisposesMessage(bool ordered)
    {
        // Arrange — PrefetchCount 1; the test reserves the only credit before RunAsync,
        // so m-0 is read and blocks in WaitForCreditAsync.
        var adapter = new HandoffFakeAdapter(messageCount: 1);
        EndpointBinding binding = BuildBinding(prefetch: 1, orderingConcurrency: ordered ? 1 : null);
        (ReceiveEndpointRunner runner, CreditManager credit, _) = CreateRunner(binding, adapter);
        credit.TryGrantCredits(1).Should().Be(1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task run = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => adapter.Yielded.Count == 1, s_waitLimit);
        await cts.CancelAsync();
        await run.WaitAsync(s_waitLimit, TestContext.Current.CancellationToken);

        // Assert — m-0 disposed and requeued exactly once; the runner released no credit it never took.
        IsDisposed(adapter.Yielded.Single()).Should().BeTrue();
        adapter.Settlements.Should().ContainSingle()
            .Which.Should().Be((SettlementAction.Requeue, "m-0"));
        credit.InflightCount.Should().Be(1, "the test's own reservation must stay untouched");
        credit.InflightBytes.Should().Be(0);
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────

    private static EndpointBinding BuildBinding(int prefetch, int? orderingConcurrency) => new()
    {
        EndpointName = QueueName,
        PrefetchCount = prefetch,
        ConcurrentMessageLimit = orderingConcurrency ?? 1,
        Ordering = orderingConcurrency is { } concurrency ? new HandoffTestOrdering(concurrency) : null,
        RawConsumers = [typeof(StalledRawConsumer)],
    };

    private static (ReceiveEndpointRunner Runner, CreditManager Credit, StallSignal Signal) CreateRunner(
        EndpointBinding binding, ITransportAdapter adapter)
    {
        var signal = new StallSignal();

        var services = new ServiceCollection();
        services.AddSingleton(signal);
        services.AddScoped<StalledRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());

        var flowController = new FlowController(NullLogger<FlowController>.Instance);

        var runner = new ReceiveEndpointRunner(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            flowController,
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        // Same options the runner passes to GetOrCreateManager, so both resolve the same CreditManager.
        CreditManager credit = flowController.GetOrCreateManager(
            binding.EndpointName,
            new FlowControlOptions { MaxInFlightMessages = binding.PrefetchCount });

        return (runner, credit, signal);
    }

    /// <summary>
    /// A message is disposed when its pooled buffer can no longer be pinned. If the pin succeeds the
    /// message is still alive: the pin is released immediately and <c>false</c> is returned.
    /// </summary>
    private static bool IsDisposed(InboundMessage message)
    {
        if (message.TryPinPooledBuffer())
        {
            message.UnpinPooledBuffer();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds. The short delay is only a polling step; the
    /// bound makes a RED run fail instead of hanging.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            try
            {
                await Task.Delay(5, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Condition was not met within {timeout}.");
            }
        }
    }

    private sealed class StallSignal
    {
        /// <summary>Resolved by the first consumer invocation to actually enter and stall.</summary>
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Never completed by the test — mirrors a permanently wedged handler that only unblocks when its
        /// own consume loop is cancelled during shutdown (<c>context.CancellationToken</c>).
        /// </summary>
        internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class StalledRawConsumer(StallSignal signal) : IRawConsumer
    {
        public async Task ConsumeAsync(RawConsumeContext context)
        {
            // TrySetResult (not SetResult): a lane redrains after cancellation and may re-enter this
            // consumer for its next queued item, and a second SetResult on an already-completed
            // TaskCompletionSource throws InvalidOperationException — which would surface as a false
            // failure of this harness rather than a signal about the runner under test.
            signal.Entered.TrySetResult();
            await signal.Gate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Minimal read-only ordering carrier with a configurable lane count.</summary>
    private sealed class HandoffTestOrdering(int concurrency) : IConsumerOrderingConfiguration
    {
        public string? HeaderName => "seq";
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency => concurrency;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public TransportAffinity TransportAffinity => TransportAffinity.None;
        public int MaxDeliveryAttempts => 0;
    }

    /// <summary>
    /// Yields <c>messageCount</c> pooled-buffer messages that all carry the same ordering key, then waits
    /// for cancellation. Each message is recorded in <see cref="Yielded"/> BEFORE <c>yield return</c>:
    /// code after a <c>yield return</c> only runs on the next <c>MoveNextAsync</c>, which a runner blocked
    /// in the handoff window never calls.
    /// </summary>
    private sealed class HandoffFakeAdapter(int messageCount) : ITransportAdapter
    {
        private readonly ConcurrentQueue<InboundMessage> _yielded = new();
        private readonly ConcurrentQueue<(SettlementAction Action, string MessageId)> _settlements = new();

        internal ConcurrentQueue<InboundMessage> Yielded => _yielded;

        internal ConcurrentQueue<(SettlementAction Action, string MessageId)> Settlements => _settlements;

        public string TransportName => "HandoffFake";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < messageCount; i++)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(MessageLength);
                var message = new InboundMessage(
                    messageId: $"m-{i}",
                    headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["seq"] = "hot" },
                    body: new ReadOnlySequence<byte>(buffer, 0, MessageLength),
                    deliveryTag: (ulong)i,
                    pooledBuffer: buffer);

                _yielded.Enqueue(message);
                yield return message;
            }

            // Block until cancellation so RunAsync stays alive through the handoff window.
            var tcs = new TaskCompletionSource();
            using (cancellationToken.Register(() => tcs.TrySetResult()))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }

        public Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            _settlements.Enqueue((action, message.MessageId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(
            TopologyDeclaration topology, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
