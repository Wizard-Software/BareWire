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

    // ── R8.6: fixed-lane key hashing ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(42)]
    [InlineData(137)]
    [InlineData(999)]
    public async Task RunAsync_OrderingOn_MultipleLanes_PreservesStrictPerKeyOrder_PropertyTest(int seed)
    {
        // Arrange — random interleave of 6 keys × 20 messages/key, PrefetchCount=32,
        // ConcurrentMessageLimit=4.
        //
        // Each message's "seq" header carries the per-key ARRIVAL index — i.e. the 0-based position of
        // that message within its key's sub-sequence in the final shuffled list. This is NOT the same as
        // an independent label: it is assigned AFTER shuffling so that "seq=0" is the first message for
        // that key to arrive at the runner, "seq=1" is the second, etc.
        //
        // Fixed-lane hashing guarantees all messages for the same key go to the same lane. The lane's
        // channel is FIFO, so messages complete in arrival order → per-key completion order must be
        // strictly 0, 1, 2, … for every key.
        //
        // With the R8.5 round-robin interim this test fails: different messages of the same key land on
        // different lanes and run in parallel → completion order scrambled → assertion violated.
        const int keyCount = 6;
        const int msgsPerKey = 20;
        const int totalMessages = keyCount * msgsPerKey;

        var rng = new Random(seed);

        // 1. Build the full pool (key, perKeyLabel) and shuffle.
        var pool = new List<(string Key, int Label)>(totalMessages);
        for (int k = 0; k < keyCount; k++)
        {
            for (int s = 0; s < msgsPerKey; s++)
            {
                pool.Add(($"key-{k}", s));
            }
        }

        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        // 2. Assign per-key arrival indices (0..msgsPerKey-1) in the shuffled order. This is the value
        //    stamped into the "seq" header and recorded by RecordingRawConsumer. After fixed-lane
        //    processing these must appear in the completion log in strictly increasing order per key.
        var perKeyCounter = new Dictionary<string, int>();
        var interleaved = new List<(string Key, int PerKeySeq)>(totalMessages);
        foreach ((string key, int _) in pool)
        {
            int arrivalIdx = perKeyCounter.GetValueOrDefault(key, 0);
            perKeyCounter[key] = arrivalIdx + 1;
            interleaved.Add((key, arrivalIdx));
        }

        var recorder = new ConcurrencyRecorder(gateDelayMs: 5);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "key"), concurrentMessageLimit: 4);

        await RunRunnerAsync(binding, recorder, new FakeAdapter(interleaved));

        // Assert — per-key arrival-index sequences must be strictly 0, 1, 2, … (0 violations).
        Dictionary<string, int[]> perKeyOrder = recorder.PerKeyCompletionOrder;
        perKeyOrder.Should().HaveCount(keyCount, "all keys must appear in the completion log");

        foreach ((string key, int[] seqs) in perKeyOrder)
        {
            seqs.Should().HaveCount(msgsPerKey, $"key '{key}' must have exactly {msgsPerKey} completions");

            for (int i = 0; i < seqs.Length; i++)
            {
                seqs[i].Should().Be(i,
                    $"key '{key}': completion[{i}]={seqs[i]} must equal {i} " +
                    $"(strict per-key FIFO order violated — fixed-lane hashing not active)");
            }
        }

        recorder.CompletedCount.Should().Be(totalMessages);
    }

    [Fact]
    public async Task RunAsync_OrderingOn_SameKey_AlwaysSameLane_SequentialPerKey()
    {
        // Arrange — single key, 24 messages, ConcurrentMessageLimit=4. Because fixed-lane hashing
        // maps the key to exactly one lane, the per-key max observed concurrency must be 1 and the
        // order must be preserved. (Round-robin would spread the messages across 4 lanes → concurrency
        // > 1 for the key → ordering violated.)
        const int msgCount = 24;
        var messages = Enumerable.Range(0, msgCount)
            .Select(i => ("same-key", i))
            .ToList();

        var recorder = new ConcurrencyRecorder(gateDelayMs: 10);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "key"), concurrentMessageLimit: 4);

        await RunRunnerAsync(binding, recorder, new FakeAdapter(messages));

        recorder.CompletedCount.Should().Be(msgCount);
        recorder.MaxObservedConcurrency.Should().Be(1,
            "all messages share the same key → same lane → sequential processing (concurrency == 1)");

        int[] completionSeqs = recorder.PerKeyCompletionOrder["same-key"];
        completionSeqs.Should().HaveCount(msgCount);
        for (int i = 1; i < completionSeqs.Length; i++)
        {
            completionSeqs[i].Should().BeGreaterThan(completionSeqs[i - 1],
                $"same-key: completion[{i}]={completionSeqs[i]} must follow completion[{i - 1}]={completionSeqs[i - 1]}");
        }
    }

    [Fact]
    public async Task RunAsync_OrderingOn_LaneCountStable_AsKeyCardinalityGrows()
    {
        // Arrange — 100 unique keys, ConcurrentMessageLimit=4. Fixed-lane hashing maps all 100 keys
        // onto exactly 4 lanes (many-to-few partition model). The max observed concurrency must be
        // ≤ 4 (lane count does not grow with key cardinality — P3: zero unbounded buffers).
        const int keyCount = 100;
        const int msgsPerKey = 3;
        const int totalMessages = keyCount * msgsPerKey;

        var messages = Enumerable.Range(0, keyCount)
            .SelectMany(k => Enumerable.Range(0, msgsPerKey).Select(s => ($"k{k:D3}", s)))
            .ToList();

        var recorder = new ConcurrencyRecorder(gateDelayMs: 5);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "key"), concurrentMessageLimit: 4);

        await RunRunnerAsync(binding, recorder, new FakeAdapter(messages));

        recorder.CompletedCount.Should().Be(totalMessages);
        recorder.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(4,
            "fixed-lane count is determined by ConcurrentMessageLimit (4), not by key cardinality (100)");
    }

    [Fact]
    public async Task RunAsync_OrderingOn_DifferentKeys_RunConcurrently()
    {
        // Arrange — many distinct keys, gated handler (gateDelayMs=50), ConcurrentMessageLimit=4.
        // With 4 distinct keys each on its own lane, all 4 lanes should process in parallel.
        // MaxObservedConcurrency must be > 1 — fixed-lane must NOT serialise the entire stream (C1).
        const int keyCount = 4;
        const int msgsPerKey = 6;

        var messages = Enumerable.Range(0, msgsPerKey)
            .SelectMany(s => Enumerable.Range(0, keyCount).Select(k => ($"lane-key-{k}", s)))
            .ToList();

        var recorder = new ConcurrencyRecorder(gateDelayMs: 50);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "key"), concurrentMessageLimit: 4);

        await RunRunnerAsync(binding, recorder, new FakeAdapter(messages));

        recorder.CompletedCount.Should().Be(keyCount * msgsPerKey);
        recorder.MaxObservedConcurrency.Should().BeGreaterThan(1,
            "distinct keys on different lanes must run in parallel (C1: ConcurrentMessageLimit is load-bearing)");
        recorder.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(keyCount,
            "concurrency is bounded by the number of distinct lanes in play");
    }

    [Fact]
    public async Task RunAsync_OrderingOn_KeylessMessages_PassThroughParallel()
    {
        // Arrange — no key header configured (HeaderName="no-such-header-exists"), UseCorrelationId=false.
        // All messages are keyless → round-robin passthrough → they flow in parallel across all lanes
        // without ordering guarantees and without deadlock.
        const int msgCount = 16;

        var recorder = new ConcurrencyRecorder(gateDelayMs: 30);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "no-such-header-exists"), concurrentMessageLimit: 4);

        await RunRunnerAsync(binding, recorder, messageCount: msgCount);

        recorder.CompletedCount.Should().Be(msgCount, "all keyless messages must complete");
        recorder.MaxObservedConcurrency.Should().BeGreaterThan(1,
            "keyless messages use round-robin passthrough → parallel across lanes (no deadlock)");
    }

    [Fact]
    public async Task RunAsync_OrderingOn_CorrelationIdSource_ResolvesAndOrders()
    {
        // Arrange — UseCorrelationId=true, messages stamped with "correlation-id" header (kebab-case,
        // the canonical consumer-side header confirmed in ConsumeContext.cs:179). Same correlation-id
        // → same lane → per-key order preserved.
        //
        // This test MUST FAIL if the resolver uses the wrong "CorrelationId" (PascalCase) literal —
        // in that case the header is never found, all messages fall through to keyless (round-robin),
        // and per-key ordering is violated. This guards GAP-1 from the plan.
        const int keyCount = 4;
        const int msgsPerKey = 15;
        const int totalMessages = keyCount * msgsPerKey;

        // Build pool (key label only; perKeySeq assigned after shuffling to represent arrival order).
        var rng = new Random(77);
        var keyPool = Enumerable.Range(0, keyCount)
            .SelectMany(k => Enumerable.Repeat($"corr-id-{k}", msgsPerKey))
            .ToList();

        // Shuffle key pool.
        for (int i = keyPool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (keyPool[i], keyPool[j]) = (keyPool[j], keyPool[i]);
        }

        // Assign per-key arrival indices (0..msgsPerKey-1) in shuffled order — stamped as "seq" header.
        var perKeyIdx = new Dictionary<string, int>();
        var messages = new List<(string Key, int PerKeySeq)>(totalMessages);
        foreach (string key in keyPool)
        {
            int arrivalIdx = perKeyIdx.GetValueOrDefault(key, 0);
            perKeyIdx[key] = arrivalIdx + 1;
            messages.Add((key, arrivalIdx));
        }

        // Stamp messages with the canonical kebab-case "correlation-id" header.
        var adapter = new FakeAdapter(messages, keyHeader: "correlation-id");

        var recorder = new ConcurrencyRecorder(gateDelayMs: 5);

        // TestOrdering: UseCorrelationId=true, HeaderName=null — resolver must read "correlation-id".
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(useCorrelationId: true), concurrentMessageLimit: 4);

        await RunRunnerAsync(binding, recorder, adapter);

        // Per-key (per-correlation-id) arrival-index sequences must be strictly 0, 1, 2, …
        // (0 violations). If the resolver used wrong "CorrelationId" (PascalCase), messages fall
        // through to keyless (round-robin across lanes) → arrival-index order scrambled → test fails.
        Dictionary<string, int[]> perKeyOrder = recorder.PerKeyCompletionOrder;
        perKeyOrder.Should().HaveCount(keyCount, "all correlation-id keys must appear in the completion log");

        foreach ((string key, int[] seqs) in perKeyOrder)
        {
            seqs.Should().HaveCount(msgsPerKey, $"corr-id '{key}' must have exactly {msgsPerKey} completions");

            for (int i = 0; i < seqs.Length; i++)
            {
                seqs[i].Should().Be(i,
                    $"corr-id '{key}': completion[{i}]={seqs[i]} must equal {i} " +
                    $"(resolver used wrong header literal — GAP-1 guard)");
            }
        }

        recorder.CompletedCount.Should().Be(totalMessages);
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

    private static Task RunRunnerAsync(
        EndpointBinding binding, ConcurrencyRecorder recorder, int messageCount)
        => RunRunnerAsync(binding, recorder, new FakeAdapter(messageCount));

    private static async Task RunRunnerAsync(
        EndpointBinding binding, ConcurrencyRecorder recorder, FakeAdapter adapter)
    {
        int messageCount = adapter.MessageCount;

        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<RecordingRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

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
        private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _perKeyOrder = new();
        private readonly TaskCompletionSource _allDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _target = int.MaxValue;

        internal int MaxObservedConcurrency => Volatile.Read(ref _max);

        internal int CompletedCount => _completionOrder.Count;

        internal int[] CompletionOrder => [.. _completionOrder];

        /// <summary>
        /// Returns the per-key completion order as key → ordered list of per-key sequence numbers.
        /// </summary>
        internal Dictionary<string, int[]> PerKeyCompletionOrder =>
            _perKeyOrder.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToArray());

        internal async Task RecordAsync(int seq, string? key = null)
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

            if (key is not null)
            {
                _perKeyOrder.GetOrAdd(key, _ => new ConcurrentQueue<int>()).Enqueue(seq);
            }

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

            // "key" is the primary recording key header; fall back to "correlation-id" so the
            // CorrelationIdSource test can record per-key order without duplicating header stamping.
            if (!context.Headers.TryGetValue("key", out string? key))
            {
                context.Headers.TryGetValue("correlation-id", out key);
            }

            await recorder.RecordAsync(seq, key).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fake transport adapter that yields empty-bodied messages stamped with a monotonic "seq" header
    /// and, optionally, a "key" header for per-key ordering tests.
    /// Tracks settlement count so the harness can deterministically stop the runner.
    /// </summary>
    private sealed class FakeAdapter : ITransportAdapter
    {
        // Each entry is (globalSeq, key?, perKeySeq, keyHeader). key==null means no key header stamped.
        private readonly List<(int GlobalSeq, string? Key, int PerKeySeq, string KeyHeader)> _messages;
        private int _settled;
        private readonly TaskCompletionSource _allSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _settleTarget = int.MaxValue;

        /// <summary>
        /// Creates an adapter that yields <paramref name="messageCount"/> messages stamped only with a
        /// monotonic "seq" header (no "key" header) — backward-compatible with R8.5 tests.
        /// </summary>
        internal FakeAdapter(int messageCount)
        {
            var msgs = new List<(int, string?, int, string)>(messageCount);
            for (int i = 0; i < messageCount; i++)
            {
                msgs.Add((i, null, i, "key"));
            }

            _messages = msgs;
        }

        /// <summary>
        /// Creates an adapter that yields messages in the provided order. Each entry is
        /// (key, perKeySeq) — the global seq is the index in the list. Messages are stamped with
        /// a "key" header (the key value) and a "seq" header (perKeySeq).
        /// </summary>
        internal FakeAdapter(IReadOnlyList<(string Key, int PerKeySeq)> messages)
        {
            var msgs = new List<(int, string?, int, string)>(messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                msgs.Add((i, messages[i].Key, messages[i].PerKeySeq, "key"));
            }

            _messages = msgs;
        }

        /// <summary>
        /// Creates an adapter that yields messages in the provided order using a custom key header name.
        /// Each entry is (key, perKeySeq).
        /// </summary>
        internal FakeAdapter(IReadOnlyList<(string Key, int PerKeySeq)> messages, string keyHeader)
        {
            var msgs = new List<(int, string?, int, string)>(messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                msgs.Add((i, messages[i].Key, messages[i].PerKeySeq, keyHeader));
            }

            _messages = msgs;
        }

        internal int MessageCount => _messages.Count;

        public string TransportName => "Fake";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < _messages.Count; i++)
            {
                (int globalSeq, string? key, int perKeySeq, string keyHeader) = _messages[i];
                var headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["seq"] = perKeySeq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };

                if (key is not null)
                {
                    headers[keyHeader] = key;
                }

                yield return new InboundMessage(
                    messageId: Guid.NewGuid().ToString(),
                    headers: headers,
                    body: ReadOnlySequence<byte>.Empty,
                    deliveryTag: (ulong)globalSeq);
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
    private sealed class TestOrdering : IConsumerOrderingConfiguration
    {
        internal TestOrdering(string? headerName = null, bool useCorrelationId = false)
        {
            HeaderName = headerName;
            UseCorrelationId = useCorrelationId;
        }

        public string? HeaderName { get; }
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId { get; }
        public int? Concurrency => null;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public global::BareWire.Abstractions.Configuration.TransportAffinity TransportAffinity
            => global::BareWire.Abstractions.Configuration.TransportAffinity.None;
        public int MaxDeliveryAttempts => 0;
    }
}
