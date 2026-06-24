using System.Buffers;
using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.Configuration;
using BareWire.FlowControl;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// R8.5 — keyed concurrency (C1) + arrival sequence (C2) in <see cref="ReceiveEndpointRunner"/>, plus the
/// D-1 shared read-only ordering carrier. Verifies that:
/// <list type="bullet">
/// <item>per-key ordering OFF (no <c>Ordering</c> on the binding) keeps the pump strictly sequential —
/// byte-for-byte pre-per-key-ordering behavior — regardless of <c>ConcurrentMessageLimit</c>;</item>
/// <item>per-key ordering ON with a single lane (<c>ConcurrentMessageLimit = 1</c>) processes messages in
/// strict arrival order (C2 FIFO anchor);</item>
/// <item>per-key ordering ON with <c>ConcurrentMessageLimit &gt; 1</c> actually runs lanes in parallel
/// (C1 — <c>ConcurrentMessageLimit</c> is now load-bearing);</item>
/// <item>the dispatch engine reads ordering from <see cref="EndpointBinding.Ordering"/> through the shared
/// <see cref="IConsumerOrderingConfiguration"/> interface for BOTH the Core and RabbitMQ carriers (D-1 —
/// no transport-local downcast).</item>
/// </list>
/// </summary>
public sealed class ReceiveEndpointRunnerOrderingTests
{
    // ── D-1: shared read-only carrier exposed on the binding (no downcast) ───────────────────────

    [Fact]
    public void AddBareWireRabbitMq_OrderedByHeader_FlowsOrderingIntoBindingAsSharedInterface()
    {
        // Arrange — configure a RabbitMQ endpoint with per-key ordering by header.
        var services = new ServiceCollection();
        services.AddBareWireRabbitMq(cfg =>
        {
            cfg.Host("amqp://guest:guest@localhost:5672/");
            cfg.ReceiveEndpoint("ordered-queue", e =>
            {
                e.ConcurrentMessageLimit = 4;
                e.OrderedByHeader("ordering-key");
            });
        });

        // Act
        IReadOnlyList<EndpointBinding> bindings =
            services.BuildServiceProvider().GetRequiredService<IReadOnlyList<EndpointBinding>>();

        // Assert — the engine reads the RabbitMQ package-local carrier THROUGH the shared interface.
        EndpointBinding binding = bindings.Single(b => b.EndpointName == "ordered-queue");
        binding.Ordering.Should().NotBeNull("OrderedByHeader was called — the ordered path must not be dead");
        IConsumerOrderingConfiguration ordering = binding.Ordering!;
        ordering.HeaderName.Should().Be("ordering-key");
        ordering.Strategy.Should().Be(ConsumerOrderingStrategy.Auto);
        binding.ConcurrentMessageLimit.Should().Be(4);
    }

    [Fact]
    public void AddBareWireRabbitMq_NoOrderedBy_LeavesOrderingNull()
    {
        // Arrange — no OrderedBy: per-key ordering OFF (the default).
        var services = new ServiceCollection();
        services.AddBareWireRabbitMq(cfg =>
        {
            cfg.Host("amqp://guest:guest@localhost:5672/");
            cfg.ReceiveEndpoint("plain-queue", _ => { });
        });

        // Act
        IReadOnlyList<EndpointBinding> bindings =
            services.BuildServiceProvider().GetRequiredService<IReadOnlyList<EndpointBinding>>();

        // Assert
        EndpointBinding binding = bindings.Single(b => b.EndpointName == "plain-queue");
        binding.Ordering.Should().BeNull("no OrderedBy was called — per-key ordering is OFF by default");
    }

    [Fact]
    public void CoreConsumerOrderingConfiguration_ImplementsSharedReadOnlyInterface()
    {
        // Arrange — the Core carrier must also be readable through the shared interface (symmetry with
        // the RabbitMQ carrier; otherwise an in-memory / non-RabbitMQ binding's ordered path would die).
        var endpoint = new ReceiveEndpointConfiguration("core-queue");
        endpoint.OrderedBy(o =>
        {
            o.ByHeader("ordering-key");
            o.Concurrency(3);
            o.Strategy(ConsumerOrderingStrategy.LocalPartitioned);
        });

        // Assert — the concrete Core carrier is assignable to the shared read-only interface and exposes
        // the configured values through it.
        endpoint.Ordering.Should().BeAssignableTo<IConsumerOrderingConfiguration>();
        var ordering = (IConsumerOrderingConfiguration)endpoint.Ordering!;
        ordering.HeaderName.Should().Be("ordering-key");
        ordering.Concurrency.Should().Be(3);
        ordering.Strategy.Should().Be(ConsumerOrderingStrategy.LocalPartitioned);
    }

    // ── Default-OFF: strict sequential pump regardless of ConcurrentMessageLimit ─────────────────

    [Fact]
    public async Task RunAsync_OrderingOff_ProcessesSequentially_EvenWithHighConcurrentMessageLimit()
    {
        // Arrange — 8 messages, ConcurrentMessageLimit = 4, but ordering OFF: the pump must stay sequential
        // (handler N+1 never overlaps handler N), proving the new ordered branch does NOT leak into OFF.
        var recorder = new ConcurrencyRecorder(gateDelayMs: 25);
        EndpointBinding binding = BuildBinding(
            ordering: null, concurrentMessageLimit: 4);

        await RunRunnerAsync(binding, recorder, messageCount: 8);

        recorder.MaxObservedConcurrency.Should().Be(1, "per-key ordering OFF must keep the pump sequential");
        recorder.CompletedCount.Should().Be(8);
    }

    // ── C2: single lane processes in strict arrival order (FIFO anchor) ──────────────────────────

    [Fact]
    public async Task RunAsync_OrderingOn_SingleLane_PreservesArrivalOrder()
    {
        // Arrange — ordering ON, ConcurrentMessageLimit = 1 → exactly one lane → strict FIFO over arrival
        // order. The recorder stamps the per-message "seq" header value into its completion log.
        var recorder = new ConcurrencyRecorder(gateDelayMs: 5);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "seq"), concurrentMessageLimit: 1);

        await RunRunnerAsync(binding, recorder, messageCount: 20);

        recorder.CompletedCount.Should().Be(20);
        recorder.MaxObservedConcurrency.Should().Be(1, "a single lane runs strictly sequentially");
        recorder.CompletionOrder.Should().Equal(
            Enumerable.Range(0, 20).ToArray(),
            "a single ordered lane must preserve the arrival order (C2 FIFO anchor)");
    }

    // ── C1: ConcurrentMessageLimit > 1 yields real cross-lane parallelism ────────────────────────

    [Fact]
    public async Task RunAsync_OrderingOn_MultipleLanes_RunsConcurrently()
    {
        // Arrange — ordering ON, ConcurrentMessageLimit = 4 → up to 4 lanes processing in parallel. With a
        // gated handler we must observe concurrency > 1 (C1: ConcurrentMessageLimit is load-bearing). The
        // round-robin interim lane assignment spreads 16 messages across 4 lanes evenly.
        var recorder = new ConcurrencyRecorder(gateDelayMs: 50);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "seq"), concurrentMessageLimit: 4);

        await RunRunnerAsync(binding, recorder, messageCount: 16);

        recorder.CompletedCount.Should().Be(16);
        recorder.MaxObservedConcurrency.Should().BeGreaterThan(1,
            "ConcurrentMessageLimit > 1 must make the ordered path run lanes in parallel (C1)");
        recorder.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(4,
            "concurrency is capped at ConcurrentMessageLimit lanes");
    }

    // ── Shutdown: ordered lanes are drained on cancellation (no leaked workers / orphaned messages) ──

    [Fact]
    public async Task RunAsync_OrderingOn_Cancellation_DrainsLanes_AndCompletesPromptly()
    {
        // Arrange — ordering ON with multiple lanes and a slow handler so messages are still in-flight on
        // the lanes when cancellation fires. The drain (CompleteAsync in the runner's finally) must complete
        // the lane writers so the lane readers finish and Task.WhenAll joins — otherwise RunAsync would hang
        // and lane workers would leak. We assert RunAsync returns well within the harness timeout.
        var recorder = new ConcurrencyRecorder(gateDelayMs: 20);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "seq"), concurrentMessageLimit: 4);

        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<RecordingRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var adapter = new FakeAdapter(messageCount: 24);
        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());
        var runner = new ReceiveEndpointRunner(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FlowController(NullLogger<FlowController>.Instance),
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using var cts = new CancellationTokenSource();
        Task runTask = runner.RunAsync(cts.Token);

        // Let a few messages start, then cancel while lanes still have queued/in-flight work.
        await Task.Delay(40);
        await cts.CancelAsync();

        // Act + Assert — RunAsync must complete (drain + join) and not hang. A 10s budget is generous; a
        // leaked/blocked lane worker (the PERF-1 bug) would make this time out.
        Func<Task> act = async () => await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().NotThrowAsync<TimeoutException>(
            "ordered lanes must be drained on cancellation so RunAsync completes without leaking workers");
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────

    private static EndpointBinding BuildBinding(
        IConsumerOrderingConfiguration? ordering,
        int concurrentMessageLimit)
        => new()
        {
            EndpointName = "test-endpoint",
            PrefetchCount = 32,
            ConcurrentMessageLimit = concurrentMessageLimit,
            Ordering = ordering,
            RawConsumers = [typeof(RecordingRawConsumer)],
        };

    private static async Task RunRunnerAsync(
        EndpointBinding binding, ConcurrencyRecorder recorder, int messageCount)
    {
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<RecordingRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var adapter = new FakeAdapter(messageCount);
        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        var sendEndpointProvider = Substitute.For<ISendEndpointProvider>();
        var flowController = new FlowController(NullLogger<FlowController>.Instance);

        var runner = new ReceiveEndpointRunner(
            binding,
            adapter,
            deserializerResolver,
            publishEndpoint,
            sendEndpointProvider,
            provider.GetRequiredService<IServiceScopeFactory>(),
            flowController,
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Stop the runner once all messages have been settled; the fake adapter blocks after yielding.
        Task runTask = runner.RunAsync(cts.Token);
        await recorder.WaitForCompletionAsync(messageCount, cts.Token);
        await adapter.SettledAsync(messageCount, cts.Token);
        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            // Expected — cancellation stops the consume loop.
        }
    }

    /// <summary>Records observed concurrency and completion order across all lanes/handlers.</summary>
    private sealed class ConcurrencyRecorder(int gateDelayMs)
    {
        private int _current;
        private int _max;
        private readonly object _gate = new();
        private readonly ConcurrentQueue<int> _completionOrder = new();
        private readonly TaskCompletionSource _allDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _target = int.MaxValue;

        internal int MaxObservedConcurrency => Volatile.Read(ref _max);

        internal int CompletedCount => _completionOrder.Count;

        internal int[] CompletionOrder => [.. _completionOrder];

        internal async Task RecordAsync(int seq)
        {
            int now = Interlocked.Increment(ref _current);
            lock (_gate)
            {
                if (now > _max)
                {
                    _max = now;
                }
            }

            // Hold the slot briefly so parallel lanes overlap observably (and a single lane cannot).
            await Task.Delay(gateDelayMs).ConfigureAwait(false);

            Interlocked.Decrement(ref _current);
            _completionOrder.Enqueue(seq);
            if (_completionOrder.Count >= Volatile.Read(ref _target))
            {
                _allDone.TrySetResult();
            }
        }

        internal async Task WaitForCompletionAsync(int count, CancellationToken ct)
        {
            Volatile.Write(ref _target, count);
            if (_completionOrder.Count >= count)
            {
                _allDone.TrySetResult();
            }

            await _allDone.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Raw consumer that records its invocation order + observed concurrency.</summary>
    private sealed class RecordingRawConsumer(ConcurrencyRecorder recorder) : IRawConsumer
    {
        public async Task ConsumeAsync(RawConsumeContext context)
        {
            int seq = context.Headers.TryGetValue("seq", out string? raw)
                ? int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)
                : -1;
            await recorder.RecordAsync(seq).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fake transport adapter that yields <c>messageCount</c> empty-bodied messages (each stamped with a
    /// monotonic "seq" header) then blocks until cancellation. Tracks settlement count so the harness can
    /// deterministically stop the runner.
    /// </summary>
    private sealed class FakeAdapter(int messageCount) : ITransportAdapter
    {
        private int _settled;
        private readonly TaskCompletionSource _allSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _settleTarget = int.MaxValue;

        public string TransportName => "Fake";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < messageCount; i++)
            {
                var headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["seq"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                yield return new InboundMessage(
                    messageId: Guid.NewGuid().ToString(),
                    headers: headers,
                    body: ReadOnlySequence<byte>.Empty,
                    deliveryTag: (ulong)i);
            }

            // Block until cancellation so RunAsync stays alive through settlement.
            var tcs = new TaskCompletionSource();
            using (cancellationToken.Register(() => tcs.TrySetResult()))
            {
                await tcs.Task.ConfigureAwait(false);
            }

            yield break;
        }

        public Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            int now = Interlocked.Increment(ref _settled);
            if (now >= Volatile.Read(ref _settleTarget))
            {
                _allSettled.TrySetResult();
            }

            return Task.CompletedTask;
        }

        internal async Task SettledAsync(int count, CancellationToken ct)
        {
            Volatile.Write(ref _settleTarget, count);
            if (Volatile.Read(ref _settled) >= count)
            {
                _allSettled.TrySetResult();
            }

            await _allSettled.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(
            global::BareWire.Abstractions.Topology.TopologyDeclaration topology,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>Minimal read-only ordering carrier for driving the runner's ordered path directly.</summary>
    private sealed class TestOrdering(string headerName) : IConsumerOrderingConfiguration
    {
        public string? HeaderName => headerName;
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency => null;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public global::BareWire.Abstractions.Configuration.TransportAffinity TransportAffinity
            => global::BareWire.Abstractions.Configuration.TransportAffinity.None;
        public int MaxDeliveryAttempts => 0;
    }
}
