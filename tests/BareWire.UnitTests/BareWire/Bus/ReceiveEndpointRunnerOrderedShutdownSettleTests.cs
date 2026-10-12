using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using BareWire.UnitTests.Transport;
using BareWire.UnitTests.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Pins the shutdown behavior of the ordered (per-key) dispatch path of <c>ReceiveEndpointRunner</c>:
/// a lane that finishes its message after the runner was told to stop must still settle it with a live
/// token (bounded by a settlement budget), a failing settlement is logged once and does not stop the
/// lane, and the read loop never pulls another message once a stop is requested. The runner is driven
/// against a real in-memory transport wrapped by small recording or hanging decorators.
/// </summary>
public sealed class ReceiveEndpointRunnerOrderedShutdownSettleTests
{
    private const string QueueName = "ordered-shutdown-settle";

    // Mirrors the lane settlement budget of the runner: a settlement token is cancelled this long after
    // the runner's own token was cancelled.
    private static readonly TimeSpan OrderedSettlementBudget = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    // ── Settlement token ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RunAsync_OrderedLaneCompletesAfterStop_SettlesWithLiveToken(int concurrency)
    {
        // Arrange — one queued message; the consumer ignores its token and blocks until released.
        InMemoryTransportAdapter transport = CreateTransport();
        Enqueue(GetQueue(transport), "m-0", "k-0");
        var adapter = new SettleTokenRecordingAdapter(transport);
        var gate = new GateState();
        ReceiveEndpointRunner runner = CreateRunner(adapter, gate, NullLogger<ReceiveEndpointRunner>.Instance, concurrency);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act — stop while the lane still owns the message, then let the consumer finish.
        Task runTask = runner.RunAsync(cts.Token);
        await gate.Entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        gate.Release.SetResult();
        await runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        adapter.Settlements.Should().ContainSingle(s => s.Action == SettlementAction.Ack)
            .Which.TokenCancelledAtEntry.Should().BeFalse("a lane settles after stop and must still reach the broker");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RunAsync_OrderedStopWithMessageInLane_AckedOnceAndNotRedelivered(int concurrency)
    {
        // Arrange — one message, so the outcome does not depend on how many the runner managed to pull; the
        // consumer ignores its token and blocks until released. The transport signals when its stopping
        // consume call starts waiting for the lane's settlement.
        var transportLogger = new WaitSignalingLogger();
        InMemoryTransportAdapter transport = CreateTransport(transportLogger);
        InMemoryQueue queue = GetQueue(transport);
        Enqueue(queue, "m-0", "k-0");
        var gate = new GateState();
        ReceiveEndpointRunner runner = CreateRunner(transport, gate, NullLogger<ReceiveEndpointRunner>.Instance, concurrency);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act — stop while the lane owns the message; the lane settles only after the transport is waiting.
        Task runTask = runner.RunAsync(cts.Token);
        await gate.Entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await transportLogger.WaitStarted.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        gate.Release.SetResult();
        await runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        queue.Occupancy.Should().Be(0, "the acked message is gone and nothing was requeued");
        transport.InFlight.Count.Should().Be(0);
        gate.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_OrderedLaneSettleHangsAfterStop_SettleTokenCancelledAfterBudget()
    {
        // Arrange — the settlement never returns on its own; time is fake so the budget is driven by the test.
        InMemoryTransportAdapter transport = CreateTransport();
        Enqueue(GetQueue(transport), "m-0", "k-0");
        var adapter = new HangingSettleAdapter(transport);
        var gate = new GateState();
        var fake = new FakeTimeProvider();
        ReceiveEndpointRunner runner = CreateRunner(
            adapter, gate, NullLogger<ReceiveEndpointRunner>.Instance, concurrency: 1, timeProvider: fake);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task runTask = runner.RunAsync(cts.Token);
        await gate.Entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        gate.Release.SetResult();
        await adapter.SettleEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert — without a fake-time advance the settlement token cannot be cancelled yet.
        runTask.IsCompleted.Should().BeFalse("the settlement is still hanging inside the budget");
        await FakeTimePump.AdvanceUntilCompletedAsync(fake, runTask, OrderedSettlementBudget, Guard);
        adapter.TokenCancelledAtEntry.Should().BeFalse();
        adapter.CancelledWhileWaiting.Should().BeTrue("bus StopAsync must stay bounded when a settlement hangs");
    }

    [Fact]
    public async Task RunAsync_OrderedLaneSettleFails_LogsDiscardedSettlementOnceAtDebug()
    {
        // Arrange — the settlement reaches the transport and then fails with a non-cancellation exception.
        InMemoryTransportAdapter transport = CreateTransport();
        Enqueue(GetQueue(transport), "m-0", "k-0");
        var adapter = new FailingSettleAdapter(transport);
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        var gate = new GateState();
        gate.Release.SetResult();
        ReceiveEndpointRunner runner = CreateRunner(adapter, gate, logger, concurrency: 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task runTask = runner.RunAsync(cts.Token);
        await adapter.SettleFailed.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        logger.Entries.Should().ContainSingle(e => e.EventId.Name == "LogSettlementDiscarded")
            .Which.Should().Match<LogEntry>(e =>
                e.Level == LogLevel.Debug
                && (string)e["ExceptionType"]! == nameof(InvalidOperationException));
    }

    // ── Stop handling in the read loop and lane error handling ───────────────────────────────────

    [Fact] // a message-specific pre-handoff fault that also requests the stop must end the pull loop
    public async Task RunAsync_OrderedStopRequestedDuringHandoffFault_DoesNotPullAnotherMessage()
    {
        // Arrange — m-0's ordering-key header read throws and, on the way, cancels the runner token.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var adapter = new PullCountingAdapter(id => id == "m-0"
            ? new FaultingHeaders("seq", new InvalidOperationException("ordering key resolution failed"), onFault: () => cts.Cancel())
            : new Dictionary<string, string>(StringComparer.Ordinal));
        var gate = new GateState();
        gate.Release.SetResult();
        ReceiveEndpointRunner runner = CreateRunner(adapter, gate, NullLogger<ReceiveEndpointRunner>.Instance, concurrency: 2);

        // Act
        await runner.RunAsync(cts.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        adapter.PulledAfterStop.Should().Be(0, "a stop that is already requested must not ask the adapter for more");
    }

    [Fact]
    public async Task RunAsync_LaneWorkerFaults_LogsOnceAndStillReleasesConsumerChannel()
    {
        // Arrange — the consumer throws (Nack), the single allowed attempt is spent, and the durable park fails.
        var adapter = new ChannelManagingParkAdapter(ParkBehavior.Throw);
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        ReceiveEndpointRunner runner = CreateParkingRunner(adapter, logger);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task runTask = runner.RunAsync(cts.Token);
        await adapter.ParkEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        Func<Task> act = () => runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync("a lane fault is logged by the lane and must not abort the shutdown");
        adapter.ReleasedChannels.Should().ContainSingle().Which.Should().Be(("ch-1", CancellationToken.None));
        logger.Entries.Count(e => e.EventId.Name == "LogLaneWorkerFaulted").Should().Be(1);
        logger.Entries.Should().NotContain(e =>
            e.EventId.Name == "LogLaneDrainFaulted" || e.EventId.Name == "LogConsumeLoopFaulted");
    }

    [Fact] // a cancelled C3 delay on a normal stop is not an error
    public async Task RunAsync_C3DelayCancelledByStop_LogsNoError()
    {
        // Arrange — the park attempt waits until the stop cancels it.
        var adapter = new ChannelManagingParkAdapter(ParkBehavior.WaitForCancellation);
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        ReceiveEndpointRunner runner = CreateParkingRunner(adapter, logger);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task runTask = runner.RunAsync(cts.Token);
        await adapter.ParkEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        Func<Task> act = () => runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync();
        adapter.ReleasedChannels.Should().ContainSingle();
        logger.Entries.Should().NotContain(e =>
            e.EventId.Name == "LogLaneWorkerFaulted" || e.EventId.Name == "LogConsumeLoopFaulted");
    }

    [Fact] // characterization: GREEN before and after
    public async Task RunAsync_ConsumeAsyncThrowsSynchronously_LogsFaultAndCompletesLanes()
    {
        // Arrange — ConsumeAsync throws before it returns an enumerable.
        var adapter = new ThrowingConsumeAdapter();
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        var gate = new GateState();
        ReceiveEndpointRunner runner = CreateRunner(adapter, gate, logger, concurrency: 2);

        // Act
        Func<Task> act = () => runner.RunAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<BareWireConfigurationException>();
        logger.Entries.Should().ContainSingle(e => e.EventId.Name == "LogConsumeLoopFaulted");
    }

    [Fact] // characterization: GREEN before and after (sequential path is untouched)
    public async Task RunAsync_SequentialGeneratorFinallyThrows_PropagatesAndReleasesChannel()
    {
        // Arrange — sequential endpoint; the generator's finally throws once the stop cancels its wait.
        var adapter = new ChannelManagingParkAdapter(ParkBehavior.Throw) { ThrowFromGeneratorFinally = true };
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        var gate = new GateState();
        gate.Release.SetResult();
        ReceiveEndpointRunner runner = CreateRunner(adapter, gate, logger, concurrency: 1, ordered: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act — wait for the (single) message to be settled by the sequential pump, then stop.
        Task runTask = runner.RunAsync(cts.Token);
        await adapter.Settled.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        Func<Task> act = () => runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Count(e => e.EventId.Name == "LogConsumeLoopFaulted").Should().Be(1);
        adapter.ReleasedChannels.Should().ContainSingle();
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────

    private static ReceiveEndpointRunner CreateRunner(
        ITransportAdapter adapter,
        GateState gate,
        ILogger logger,
        int concurrency,
        int maxDeliveryAttempts = 0,
        TimeProvider? timeProvider = null,
        bool ordered = true,
        Type? consumerType = null,
        string? deadLetterExchange = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddScoped<GatedRawConsumer>();
        services.AddScoped<ThrowingRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());

        var binding = new EndpointBinding
        {
            EndpointName = QueueName,
            PrefetchCount = 32,
            ConcurrentMessageLimit = concurrency,
            Ordering = ordered ? new SettleTestOrdering(concurrency, maxDeliveryAttempts) : null,
            RawConsumers = [consumerType ?? typeof(GatedRawConsumer)],
            DeadLetterExchange = deadLetterExchange,
            RetryCount = 1,
        };

        return new ReceiveEndpointRunner(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FlowController(NullLogger<FlowController>.Instance),
            new NullInstrumentation(),
            logger,
            timeProvider: timeProvider);
    }

    private static ReceiveEndpointRunner CreateParkingRunner(ITransportAdapter adapter, ILogger logger) =>
        CreateRunner(
            adapter,
            new GateState(),
            logger,
            concurrency: 1,
            maxDeliveryAttempts: 1,
            consumerType: typeof(ThrowingRawConsumer),
            deadLetterExchange: "dlx");

    private static InMemoryTransportAdapter CreateTransport(ILogger<InMemoryTransportAdapter>? logger = null)
    {
        var configurator = new InMemoryConfigurator();
        configurator.QueueCapacity(8);
        configurator.ConfigureTopology(t => t.DeclareQueue(QueueName));
        InMemoryTransportOptions options = configurator.Build();
        return new InMemoryTransportAdapter(options, new InMemoryBroker(options), logger);
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

    private sealed class GateState
    {
        /// <summary>Resolved by the first consumer invocation.</summary>
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the test to let the consumer return; the consumer ignores its own token.</summary>
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _invocations;

        /// <summary>Gets how many times a consumer was invoked.</summary>
        internal int Invocations => Volatile.Read(ref _invocations);

        internal void RecordInvocation() => Interlocked.Increment(ref _invocations);
    }

    /// <summary>Completes <see cref="WaitStarted"/> when a stopping in-memory consume call starts waiting for settlements.</summary>
    private sealed class WaitSignalingLogger : ILogger<InMemoryTransportAdapter>
    {
        internal TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "LogSettlementDrainWaitStarted")
            {
                WaitStarted.TrySetResult();
            }
        }
    }

    private sealed class GatedRawConsumer(GateState gate) : IRawConsumer
    {
        public async Task ConsumeAsync(RawConsumeContext context)
        {
            gate.RecordInvocation();
            gate.Entered.TrySetResult();
            await gate.Release.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Minimal read-only ordering carrier with a configurable lane count and attempt limit.</summary>
    private sealed class SettleTestOrdering(int concurrency, int maxDeliveryAttempts) : IConsumerOrderingConfiguration
    {
        public string? HeaderName => "seq";
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency => concurrency;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public TransportAffinity TransportAffinity => TransportAffinity.None;
        public int MaxDeliveryAttempts => maxDeliveryAttempts;
    }

    /// <summary>Delegating base for the settlement decorators: everything but <see cref="SettleAsync"/> passes through.</summary>
    private abstract class DecoratingAdapter(ITransportAdapter inner) : ITransportAdapter
    {
        protected ITransportAdapter Inner { get; } = inner;

        public string TransportName => Inner.TransportName;

        public TransportCapabilities Capabilities => Inner.Capabilities;

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => Inner.SendBatchAsync(messages, cancellationToken);

        public IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            CancellationToken cancellationToken = default)
            => Inner.ConsumeAsync(endpointName, flowControl, cancellationToken);

        public abstract Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default);

        public Task DeployTopologyAsync(
            TopologyDeclaration topology, CancellationToken cancellationToken = default)
            => Inner.DeployTopologyAsync(topology, cancellationToken);
    }

    /// <summary>
    /// Records, per settlement, whether the token was already cancelled on entry and — like a broker SDK —
    /// throws <see cref="OperationCanceledException"/> in that case; otherwise delegates to the real adapter.
    /// </summary>
    private sealed class SettleTokenRecordingAdapter(ITransportAdapter inner) : DecoratingAdapter(inner)
    {
        private readonly ConcurrentQueue<(string MessageId, SettlementAction Action, bool TokenCancelledAtEntry)> _settlements = new();

        internal IReadOnlyCollection<(string MessageId, SettlementAction Action, bool TokenCancelledAtEntry)> Settlements
            => _settlements;

        public override Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            _settlements.Enqueue((message.MessageId, action, cancelled));
            if (cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Inner.SettleAsync(action, message, cancellationToken);
        }
    }

    /// <summary>
    /// Waits forever on the supplied token. On cancellation it settles the same action on the real adapter
    /// with <see cref="CancellationToken.None"/> (so the in-memory transport does not wait for it) and rethrows.
    /// </summary>
    private sealed class HangingSettleAdapter(ITransportAdapter inner) : DecoratingAdapter(inner)
    {
        private int _tokenCancelledAtEntry;
        private int _cancelledWhileWaiting;

        internal TaskCompletionSource SettleEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool TokenCancelledAtEntry => Volatile.Read(ref _tokenCancelledAtEntry) == 1;

        internal bool CancelledWhileWaiting => Volatile.Read(ref _cancelledWhileWaiting) == 1;

        public override async Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _tokenCancelledAtEntry, 1);
            }

            SettleEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _cancelledWhileWaiting, 1);
                await Inner.SettleAsync(action, message, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Delegates the settlement to the real adapter and then fails with a non-cancellation exception.</summary>
    private sealed class FailingSettleAdapter(ITransportAdapter inner) : DecoratingAdapter(inner)
    {
        internal TaskCompletionSource SettleFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            await Inner.SettleAsync(action, message, cancellationToken).ConfigureAwait(false);
            SettleFailed.TrySetResult();
            throw new InvalidOperationException("broker settlement failed");
        }
    }

    private sealed class ThrowingRawConsumer : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context) => throw new InvalidOperationException("consumer failed");
    }

    /// <summary>Headers that throw (after an optional callback) only when the given key is looked up.</summary>
    private sealed class FaultingHeaders(string throwOnKey, Exception fault, Action? onFault = null)
        : IReadOnlyDictionary<string, string>
    {
        public string this[string key] =>
            TryGetValue(key, out string? value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => [];

        public IEnumerable<string> Values => [];

        public int Count => 0;

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value)
        {
            if (key == throwOnKey)
            {
                onFault?.Invoke();
                throw fault;
            }

            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static InboundMessage NewMessage(string messageId, IReadOnlyDictionary<string, string> headers, ulong tag)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
        return new InboundMessage(
            messageId: messageId,
            headers: headers,
            body: new ReadOnlySequence<byte>(buffer, 0, 16),
            deliveryTag: tag,
            pooledBuffer: buffer);
    }

    /// <summary>
    /// Yields three ready messages without ever checking its token, and counts how many times the consumer
    /// asked for a next message while the token was already cancelled.
    /// </summary>
    private sealed class PullCountingAdapter(Func<string, IReadOnlyDictionary<string, string>> headersFor)
        : ITransportAdapter
    {
        private int _pulledAfterStop;

        internal int PulledAfterStop => Volatile.Read(ref _pulledAfterStop);

        public string TransportName => "PullCounting";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < 3; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _pulledAfterStop);
                }

                string id = $"m-{i}";
                yield return NewMessage(id, headersFor(id), (ulong)i);
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        public Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(TopologyDeclaration topology, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private enum ParkBehavior
    {
        Throw,
        WaitForCancellation,
    }

    /// <summary>
    /// Yields one message carrying a consumer-channel id, then waits for cancellation. Implements the durable
    /// park and channel-release seams the ordered path uses; both record what the runner did.
    /// </summary>
    private sealed class ChannelManagingParkAdapter(ParkBehavior parkBehavior)
        : ITransportAdapter, IConsumerChannelManager, IDurableParkSettlement
    {
        private readonly ConcurrentQueue<(string ChannelId, CancellationToken Token)> _released = new();

        internal TaskCompletionSource ParkEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IReadOnlyCollection<(string ChannelId, CancellationToken Token)> ReleasedChannels => _released;

        /// <summary>When set, the generator's finally throws after the wait was cancelled.</summary>
        internal bool ThrowFromGeneratorFinally { get; init; }

        public string TransportName => "ChannelManagingPark";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["BW-ConsumerChannelId"] = "ch-1" };
            yield return NewMessage("m-0", headers, 0);

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ThrowFromGeneratorFinally)
                {
#pragma warning disable CA2219 // Deliberate: models an adapter whose cleanup fails during shutdown.
                    throw new InvalidOperationException("generator cleanup failed");
#pragma warning restore CA2219
                }
            }
        }

        public async Task<DurableSettlementResult> ParkHeadDurablyAsync(
            InboundMessage message,
            string deadLetterExchange,
            string deadLetterRoutingKey,
            CancellationToken cancellationToken = default)
        {
            ParkEntered.TrySetResult();
            if (parkBehavior == ParkBehavior.Throw)
            {
                throw new InvalidOperationException("park failed");
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new DurableSettlementResult(false, "unreachable");
        }

        public Task ReleaseConsumerChannelAsync(string channelId, CancellationToken cancellationToken = default)
        {
            _released.Enqueue((channelId, cancellationToken));
            return Task.CompletedTask;
        }

        public Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            Settled.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(TopologyDeclaration topology, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>ConsumeAsync fails synchronously (not as part of enumeration).</summary>
    private sealed class ThrowingConsumeAdapter : ITransportAdapter
    {
        public string TransportName => "ThrowingConsume";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName, FlowControlOptions flowControl, CancellationToken cancellationToken = default)
            => throw new BareWireConfigurationException("consume is misconfigured");

        public Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(TopologyDeclaration topology, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
