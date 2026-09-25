using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Regression guard for a cancelled consumer that is still stalled when a receive endpoint shuts down.
/// Reproduces the scenario on a real in-memory transport (not a fake): a small queue is filled to
/// capacity, every delivery's consumer blocks on the ambient <c>CancellationToken</c>, and the runner's
/// token is cancelled while those consumers are still in flight. A counting decorator around the real
/// <see cref="InMemoryTransportAdapter"/> measures how many deliveries the runner reads and how many
/// times it requeues while shutting down, across all three dispatch shapes the runner supports:
/// the strictly sequential pump (per-key ordering OFF), a single ordered lane, and multiple ordered
/// lanes.
/// </summary>
public sealed class ReceiveEndpointRunnerShutdownRequeueTests
{
    private const string QueueName = "shutdown-requeue";
    private const int QueuedMessageCount = 5;

    public enum RunnerPath
    {
        Sequential,
        OrderedSingleLane,
        OrderedMultiLane,
    }

    [Theory]
    [InlineData(RunnerPath.Sequential)]
    [InlineData(RunnerPath.OrderedSingleLane)]
    [InlineData(RunnerPath.OrderedMultiLane)]
    public async Task RunAsync_CancelledWhileConsumerStalled_RequeuesAndReadsAtMostQueuedMessages(RunnerPath path)
    {
        // Arrange — a queue filled to capacity with distinct ordering keys, so the multi-lane path
        // spreads deliveries across lanes instead of collapsing them onto one key.
        InMemoryTransportAdapter transport = CreateTransport(queueCapacity: QueuedMessageCount);
        InMemoryQueue queue = GetQueue(transport);
        for (int i = 0; i < QueuedMessageCount; i++)
        {
            Enqueue(queue, messageId: $"m-{i}", orderingKey: $"k-{i}");
        }

        var adapter = new CountingTransportAdapter(transport);
        var signal = new StallSignal();

        var services = new ServiceCollection();
        services.AddSingleton(signal);
        services.AddScoped<StalledRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());

        var runner = new ReceiveEndpointRunner(
            BuildBinding(path),
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FlowController(NullLogger<FlowController>.Instance),
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act — start the runner, wait until at least one consumer has actually stalled on the
        // cancellation token (so a delivery is genuinely in flight), then cancel and let the shutdown
        // path unwind.
        Task runTask = runner.RunAsync(cts.Token);
        await signal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        adapter.CancellationObserved = true;
        await cts.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert — validity check: the counting decorator must have actually observed traffic, otherwise
        // the counters below prove nothing.
        adapter.TotalReads.Should().BeGreaterThanOrEqualTo(1,
            "the counting adapter must observe at least one delivery for its counters to be meaningful");

        IReadOnlyList<int> redeliveryCounts = await DrainRedeliveryCountsAsync(queue, QueuedMessageCount);
        string diagnostics =
            $"path={path}, TotalReads={adapter.TotalReads}, " +
            $"ReadsAfterCancellation={adapter.ReadsAfterCancellation}, " +
            $"RequeueSettlements={adapter.RequeueSettlements}, Occupancy={queue.Occupancy}, " +
            $"redeliveryCounts=[{string.Join(',', redeliveryCounts)}]";

        queue.Occupancy.Should().Be(QueuedMessageCount,
            $"a requeue on shutdown must not lose messages ({diagnostics})");

        adapter.RequeueSettlements.Should().BeLessThanOrEqualTo(QueuedMessageCount,
            $"a cancelled consumer must not requeue the same deliveries over and over while shutting down ({diagnostics})");

        if (path == RunnerPath.Sequential)
        {
            adapter.TotalReads.Should().BeLessThanOrEqualTo(QueuedMessageCount,
                $"a delivery requeued because of cancellation must not be read again by the stopping loop ({diagnostics})");
            adapter.ReadsAfterCancellation.Should().BeLessThanOrEqualTo(QueuedMessageCount,
                $"no delivery should be read more than once after cancellation was observed ({diagnostics})");
        }
        else
        {
            // Ordered paths: the single reader may race one extra in-flight delivery into a lane channel
            // before it observes cancellation — a deterministic +1 tolerance for that race, not for an
            // unbounded requeue/read spin.
            adapter.TotalReads.Should().BeLessThanOrEqualTo(QueuedMessageCount + 1,
                $"lane fan-out tolerates at most one extra read racing cancellation, not a requeue/read busy-spin ({diagnostics})");
        }

        redeliveryCounts.Should().HaveCount(QueuedMessageCount,
            $"every queued message must still be recoverable from the queue after shutdown ({diagnostics})");
        redeliveryCounts.Should().OnlyContain(count => count <= 1,
            $"a requeue-driven busy-spin would redeliver the same copy more than once before shutdown is observed ({diagnostics})");
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────

    private static EndpointBinding BuildBinding(RunnerPath path) => path switch
    {
        RunnerPath.Sequential => new EndpointBinding
        {
            EndpointName = QueueName,
            PrefetchCount = 32,
            ConcurrentMessageLimit = 1,
            Ordering = null,
            RawConsumers = [typeof(StalledRawConsumer)],
        },
        RunnerPath.OrderedSingleLane => new EndpointBinding
        {
            EndpointName = QueueName,
            PrefetchCount = 32,
            ConcurrentMessageLimit = 1,
            Ordering = new ShutdownTestOrdering(concurrency: 1),
            RawConsumers = [typeof(StalledRawConsumer)],
        },
        RunnerPath.OrderedMultiLane => new EndpointBinding
        {
            EndpointName = QueueName,
            PrefetchCount = 32,
            ConcurrentMessageLimit = 4,
            Ordering = new ShutdownTestOrdering(concurrency: 4),
            RawConsumers = [typeof(StalledRawConsumer)],
        },
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, message: null),
    };

    private static InMemoryTransportAdapter CreateTransport(int queueCapacity)
    {
        var configurator = new InMemoryConfigurator();
        configurator.QueueCapacity(queueCapacity);
        configurator.ConfigureTopology(t => t.DeclareQueue(QueueName));
        InMemoryTransportOptions options = configurator.Build();
        return new InMemoryTransportAdapter(options, new InMemoryBroker(options));
    }

    private static InMemoryQueue GetQueue(InMemoryTransportAdapter transport)
    {
        transport.Broker.TryGetQueue(QueueName, out InMemoryQueue? queue).Should().BeTrue();
        return queue!;
    }

    private static void Enqueue(InMemoryQueue queue, string messageId, string orderingKey)
    {
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
        int length = Encoding.UTF8.GetBytes(messageId, buffer);
        InMemoryHeaderSet headers = InMemoryHeaderSet.Stamp(
            new Dictionary<string, string>
            {
                [InMemoryHeaderNames.MessageId] = messageId,
                ["seq"] = orderingKey,
            },
            exchange: string.Empty,
            routingKey: queue.Name,
            contentType: string.Empty);
        queue.WriteReserved(new InMemoryDelivery(buffer, length, headers));
    }

    /// <summary>
    /// Reads up to <paramref name="expectedCount"/> deliveries directly off the queue's channel (bypassing
    /// the transport adapter) and returns each one's redelivery count. Used only after the runner has
    /// fully stopped, so this is the only reader left and draining does not race a live consumer. Bounded
    /// by a generous timeout: fewer deliveries than expected surfaces as a shortfall in the caller's
    /// assertion rather than a hang.
    /// </summary>
    private static async Task<IReadOnlyList<int>> DrainRedeliveryCountsAsync(InMemoryQueue queue, int expectedCount)
    {
        var counts = new List<int>(expectedCount);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await foreach (InMemoryDelivery delivery in queue.ReadAllAsync(cts.Token))
            {
                counts.Add(delivery.RedeliveryCount);
                if (counts.Count == expectedCount)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fewer than expectedCount deliveries were sitting in the queue within the timeout — the
            // shortfall is surfaced by the HaveCount assertion in the caller.
        }

        return counts;
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
    private sealed class ShutdownTestOrdering(int concurrency) : IConsumerOrderingConfiguration
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
    /// Transparently delegates every member to a real <see cref="InMemoryTransportAdapter"/> while
    /// counting, via <see cref="Interlocked"/>, the deliveries yielded from <see cref="ConsumeAsync"/>
    /// (in total and after the test sets <see cref="CancellationObserved"/>) and the number of
    /// <see cref="SettleAsync"/> calls that carry <see cref="SettlementAction.Requeue"/>.
    /// </summary>
    private sealed class CountingTransportAdapter(ITransportAdapter inner) : ITransportAdapter
    {
        private int _totalReads;
        private int _readsAfterCancellation;
        private int _requeueSettlements;

        internal int TotalReads => Volatile.Read(ref _totalReads);

        internal int ReadsAfterCancellation => Volatile.Read(ref _readsAfterCancellation);

        internal int RequeueSettlements => Volatile.Read(ref _requeueSettlements);

        /// <summary>Set by the test immediately before cancelling the runner's token.</summary>
        internal volatile bool CancellationObserved;

        public string TransportName => inner.TransportName;

        public TransportCapabilities Capabilities => inner.Capabilities;

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => inner.SendBatchAsync(messages, cancellationToken);

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (InboundMessage message in inner
                .ConsumeAsync(endpointName, flowControl, cancellationToken)
                .ConfigureAwait(false))
            {
                Interlocked.Increment(ref _totalReads);
                if (CancellationObserved)
                {
                    Interlocked.Increment(ref _readsAfterCancellation);
                }

                yield return message;
            }
        }

        public Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            if (action == SettlementAction.Requeue)
            {
                Interlocked.Increment(ref _requeueSettlements);
            }

            return inner.SettleAsync(action, message, cancellationToken);
        }

        public Task DeployTopologyAsync(
            TopologyDeclaration topology, CancellationToken cancellationToken = default)
            => inner.DeployTopologyAsync(topology, cancellationToken);
    }
}
