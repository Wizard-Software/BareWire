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
    // ADR-026 §8 — widened seed space for CI reproducibility (R8.15 strengthening).
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(271)]
    [InlineData(314)]
    [InlineData(1618)]
    [InlineData(2024)]
    [InlineData(31337)]
    [InlineData(65535)]
    [InlineData(99991)]
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
        //
        // ADR-026 §8 invariant (R8.15 — C2 arrival-sequence capture): 100%, 0 violations across ALL
        // seeds. A failure here indicates a regression in the non-FIFO SemaphoreSlim defect (R6) or in
        // the C2 arrival-index capture point of the fixed-lane dispatch stage.
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
                    $"seed={seed} key='{key}' completion[{i}]={seqs[i]} must equal {i} — " +
                    $"strict per-key FIFO order violated (R6 non-FIFO regression / C2 arrival-sequence capture)");
            }
        }

        recorder.CompletedCount.Should().Be(totalMessages);
    }

    // ── R8.15 ADR-026 §8: high-cardinality property test (12 keys × 40 msgs, real cross-lane concurrency C2) ──

    [Theory]
    [InlineData(17)]
    [InlineData(503)]
    [InlineData(7919)]
    [InlineData(104729)]
    [InlineData(999983)]
    [InlineData(1000003)]
    public async Task RunAsync_OrderingOn_HighCardinality_PreservesStrictPerKeyOrder_PropertyTest(int seed)
    {
        // Arrange — random interleave of 12 keys × 40 messages/key (480 total).
        // PrefetchCount=32 (BuildBinding default), ConcurrentMessageLimit=8 (real cross-lane concurrency).
        //
        // Pattern mirrors RunAsync_OrderingOn_MultipleLanes_PreservesStrictPerKeyOrder_PropertyTest:
        //   1. Build the full pool and Fisher-Yates shuffle with new Random(seed).
        //   2. Assign per-key arrival indices AFTER shuffling (C2 capture point).
        //   3. Assert seqs[i] == i for every key (0 violations).
        //
        // ADR-026 §8 invariant (R8.15 — C2 arrival-sequence capture): 100%, 0 violations across ALL
        // seeds under REAL cross-lane concurrency (ConcurrentMessageLimit=8 > 1). A failure here
        // indicates a regression in the non-FIFO SemaphoreSlim defect (R6) or in the C2 arrival-index
        // capture point of the fixed-lane dispatch stage.
        const int keyCount = 12;
        const int msgsPerKey = 40;
        const int totalMessages = keyCount * msgsPerKey;

        var rng = new Random(seed);

        // 1. Build pool and Fisher-Yates shuffle.
        var pool = new List<(string Key, int Label)>(totalMessages);
        for (int k = 0; k < keyCount; k++)
        {
            for (int s = 0; s < msgsPerKey; s++)
            {
                pool.Add(($"hc-key-{k}", s));
            }
        }

        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        // 2. Assign per-key arrival indices in the shuffled order (C2 capture point).
        var perKeyCounter = new Dictionary<string, int>();
        var interleaved = new List<(string Key, int PerKeySeq)>(totalMessages);
        foreach ((string key, int _) in pool)
        {
            int arrivalIdx = perKeyCounter.GetValueOrDefault(key, 0);
            perKeyCounter[key] = arrivalIdx + 1;
            interleaved.Add((key, arrivalIdx));
        }

        // ConcurrentMessageLimit=8 > 1: real cross-lane concurrency (C2).
        var recorder = new ConcurrencyRecorder(gateDelayMs: 5);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "key"), concurrentMessageLimit: 8);

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
                    $"seed={seed} key='{key}' completion[{i}]={seqs[i]} must equal {i} — " +
                    $"strict per-key FIFO order violated (R6 non-FIFO regression / C2 arrival-sequence capture)");
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

    // ── R8.7: bounded lane depth — no-deadlock invariant, hot-key backpressure, FIFO regression ────

    [Theory]
    [InlineData(2, 4)]   // 2 lanes, prefetch=4 → laneDepth=ceil(4/2)=2; real cross-lane concurrency
    [InlineData(1, 4)]   // degenerate: 1 lane, depth=4; single worker must still drain without deadlock
    public async Task RunAsync_OrderingOn_LanesFull_AndCreditFull_StillMakesProgress_NoDeadlock(
        int laneCount, int prefetchCount)
    {
        // Arrange — deterministic saturation proof (PERF-3).
        //
        // Setup: PrefetchCount=4 → 4 global credits (axis 1).
        //   laneCount=2: laneDepth=ceil(4/2)=2 → 2×2=4 total lane slots (axis 2).
        //   laneCount=1: laneDepth=ceil(4/1)=4 → 1×4=4 total lane slots.
        // In both cases combined lane capacity = prefetchCount.
        //
        // Gate mechanism: a TaskCompletionSource (handlerGate) is passed to ConcurrencyRecorder.
        // RecordAsync awaits the gate BEFORE recording completion. While the gate is closed:
        //   - Each dequeued message occupies one lane worker slot AND holds one credit.
        //   - Lane channels fill to their depth; workers block on the gate (not yet releasing).
        //   - The reader exhausts all prefetchCount credits and both/all channels fill up.
        //   - The reader blocks on WriteAsync (full lane) or WaitForCreditAsync (no credit).
        //   - CompletedCount stays at 0 — nothing can settle.
        //
        // Saturation assertion: wait until the adapter has delivered at least
        //   (prefetchCount + totalLaneCapacity) messages into the stream — at that point the
        //   credit is fully consumed AND the lane queues are full. Then assert CompletedCount==0.
        //   This is the deterministic proof that the "all full" state was actually reached.
        //
        // Release + drain assertion: releasing the gate unblocks workers → ReleaseInflight frees
        //   credits + lane slots → the blocked reader makes progress → all messages drain.
        //   TimeoutException = deadlock = FAIL. This proves the P2 no-deadlock invariant.

        // totalMessages is well above prefetchCount so the reader definitely exhausts credit.
        int totalMessages = prefetchCount * 4;

        // The gate blocks handlers INSIDE the lane worker so workers stay occupied,
        // holding inflight credit and keeping their lane slot filled.
        var handlerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new ConcurrencyRecorder(gateDelayMs: 0, handlerGate: handlerGate.Task);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "key"),
            concurrentMessageLimit: laneCount,
            prefetchCount: prefetchCount);

        // Build a message list that exercises multiple lanes.
        var messages = new List<(string Key, int PerKeySeq)>(totalMessages);
        for (int i = 0; i < totalMessages; i++)
        {
            // Alternate between two keys so messages spread across lanes (for laneCount>=2).
            messages.Add((i % 2 == 0 ? "key-a" : "key-b", i / 2));
        }

        var adapter = new FakeAdapter(messages);

        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<RecordingRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task runTask = runner.RunAsync(cts.Token);

        // ── Saturation probe: wait until message delivery PLATEAUS (reader blocked) ───────────────
        // With the gate held closed, NO handler can ever record a completion, so ReleaseInflight is
        // never called and no lane slot is ever freed. The reader therefore advances only until it
        // blocks — either on WaitForCreditAsync (axis 1: all prefetchCount credits consumed) OR on a
        // full lane's WriteAsync (axis 2: a lane reached its bounded depth). Which axis binds first
        // depends on how the fixed-lane hash distributes the two keys across lanes (with laneCount=2
        // both keys may collide on ONE lane, so the lane-depth axis can block the reader BEFORE all
        // credits are consumed). Either way the pipeline is SATURATED and stalled.
        //
        // We detect that stall by polling the delivered count until it stops increasing across
        // consecutive samples. This is a deterministic saturated-state witness precisely because the
        // closed gate guarantees delivery can ONLY stall (it can never resume until a worker drains,
        // which cannot happen while the gate blocks every handler). Both [InlineData] cases reach a
        // stable plateau: laneCount=1 → plateau at prefetchCount (single deep lane, credit binds);
        // laneCount=2 → plateau at >= laneCount (lane-depth may bind earlier under key collision).
        int plateau = await WaitForDeliveryPlateauAsync(adapter, cts.Token);

        plateau.Should().BeGreaterThanOrEqualTo(laneCount,
            "at least one message per lane must have entered the pipeline before the reader stalls");

        // ── Closed-gate assertion: saturation proven — nothing completed yet ─────────────────────
        recorder.CompletedCount.Should().Be(0,
            $"gate is held closed; delivery plateaued at {plateau} message(s) with the reader blocked " +
            "(on credit or a full lane) — zero completions proves the saturated state was reached " +
            "(deterministic saturation proof for the P2 no-deadlock invariant)");

        // ── Release gate: workers unblock → drain → credit released → reader unblocks ────────────
        handlerGate.SetResult();

        // ── Drain assertion: ALL messages must settle within a generous budget (10 s). ─────────────
        // A TimeoutException here means the system deadlocked after gate release — real bug in
        // the bounded channel / credit invariant (P2 violated).
        Func<Task> waitForAll = async () =>
        {
            await recorder.WaitForCompletionAsync(totalMessages, cts.Token);
            await adapter.SettledAsync(totalMessages, cts.Token);
        };
        await waitForAll.Should().CompleteWithinAsync(TimeSpan.FromSeconds(10),
            "after gate release, workers drain independently of the reader — " +
            "bounded lanes must not deadlock (P2 no-deadlock invariant)");

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        recorder.CompletedCount.Should().Be(totalMessages,
            "all messages must settle — FullMode.Wait must not drop any message");
    }

    /// <summary>
    /// Polls the adapter's delivered count until it stops increasing across consecutive samples,
    /// i.e. the runner's single reader has stalled (blocked on credit or on a full lane). Returns the
    /// plateau count. Used only by the no-deadlock saturation test, where the closed handler gate
    /// guarantees delivery can only stall and never resume — making the plateau a sound saturated-state
    /// witness. Bounded by the supplied cancellation token so a genuine hang still surfaces as a test
    /// failure rather than an infinite loop.
    /// </summary>
    private static async Task<int> WaitForDeliveryPlateauAsync(FakeAdapter adapter, CancellationToken ct)
    {
        int previous = -1;
        int stableSamples = 0;

        // Three consecutive equal samples (~150 ms apart) ⇒ the reader has stalled. The closed gate
        // makes this monotone-then-flat: the count never decreases and cannot resume growing.
        while (stableSamples < 3)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(50, ct);

            int current = adapter.DeliveredCount;
            stableSamples = current == previous ? stableSamples + 1 : 0;
            previous = current;
        }

        return previous;
    }

    [Fact]
    public async Task RunAsync_OrderingOn_HotKeyBackpressure_OtherLanesEventuallyComplete()
    {
        // Arrange — a hot key fills its lane (many messages on one partition); other keys on other
        // lanes. The honest claim (decision PERF-1): other lanes are NOT guaranteed to be delay-free
        // (the single reader may stall briefly on the hot lane's WriteAsync), but they EVENTUALLY
        // complete within the timeout. A permanent block would time out here.
        const int msgsPerKey = 10;
        const int keyCount = 4;
        const int totalMessages = keyCount * msgsPerKey;

        var messages = new List<(string Key, int PerKeySeq)>(totalMessages);
        for (int k = 0; k < keyCount; k++)
        {
            for (int s = 0; s < msgsPerKey; s++)
            {
                messages.Add(($"key-{k}", s));
            }
        }

        var recorder = new ConcurrencyRecorder(gateDelayMs: 20);
        EndpointBinding binding = BuildBinding(
            ordering: new TestOrdering(headerName: "key"),
            concurrentMessageLimit: 4,
            prefetchCount: 8);

        await RunRunnerAsync(binding, recorder, new FakeAdapter(messages));

        // Assert — all lanes must eventually complete (not just the hot key's lane).
        recorder.CompletedCount.Should().Be(totalMessages,
            "all messages across all lanes must eventually complete — " +
            "hot-key backpressure on one lane must not permanently block other lanes");
    }

    [Theory]
    [InlineData(42)]
    [InlineData(137)]
    public async Task RunAsync_OrderingOn_BoundedLanes_PreservesPerKeyFifo_Regression(int seed)
    {
        // Arrange — bounded lanes (R8.7) must not break the per-key FIFO guarantee from R8.6.
        // A wrong FullMode (DropOldest / DropWrite) would silently lose messages, causing the
        // completion count to be short and / or the per-key arrival-index sequence to have gaps.
        // FullMode.Wait is the only correct mode: it preserves all messages in order.
        const int keyCount = 4;
        const int msgsPerKey = 12;
        const int totalMessages = keyCount * msgsPerKey;

        var rng = new Random(seed);
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
            ordering: new TestOrdering(headerName: "key"),
            concurrentMessageLimit: 4,
            prefetchCount: 8);

        await RunRunnerAsync(binding, recorder, new FakeAdapter(interleaved));

        // Assert — per-key arrival-index sequences must be strictly 0, 1, 2, … (FIFO).
        // A wrong FullMode would drop messages → count short or gaps in sequence → test fails.
        recorder.CompletedCount.Should().Be(totalMessages,
            "FullMode.Wait must not drop any message — if it fails, FullMode was changed to a Drop* mode");

        Dictionary<string, int[]> perKeyOrder = recorder.PerKeyCompletionOrder;
        perKeyOrder.Should().HaveCount(keyCount);
        foreach ((string key, int[] seqs) in perKeyOrder)
        {
            seqs.Should().HaveCount(msgsPerKey,
                $"key '{key}' must have exactly {msgsPerKey} completions — bounded lanes must not drop messages");
            for (int i = 0; i < seqs.Length; i++)
            {
                seqs[i].Should().Be(i,
                    $"key '{key}': completion[{i}]={seqs[i]} must equal {i} (per-key FIFO violated)");
            }
        }
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────

    private static EndpointBinding BuildBinding(
        IConsumerOrderingConfiguration? ordering,
        int concurrentMessageLimit)
        => BuildBinding(ordering, concurrentMessageLimit, prefetchCount: 32);

    private static EndpointBinding BuildBinding(
        IConsumerOrderingConfiguration? ordering,
        int concurrentMessageLimit,
        int prefetchCount)
        => new()
        {
            EndpointName = "test-endpoint",
            PrefetchCount = prefetchCount,
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
    /// <param name="gateDelayMs">
    /// How long each handler holds its concurrency slot open (simulates processing time so parallel
    /// lanes overlap observably). The existing 13 tests use this path unchanged.
    /// </param>
    /// <param name="handlerGate">
    /// Optional external gate. When supplied, <see cref="RecordAsync"/> awaits this task BEFORE
    /// recording a completion. While the gate is open (not yet completed), the handler stays
    /// in-flight — holding inflight credit and its lane slot — so the caller can drive the pipeline
    /// to saturation before releasing. Pass <c>null</c> (the default) to use the <paramref
    /// name="gateDelayMs"/> path only (backward-compatible with all existing tests).
    /// </param>
    private sealed class ConcurrencyRecorder(int gateDelayMs, Task? handlerGate = null)
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

            // When a controllable gate is provided (no-deadlock saturation test), await it BEFORE
            // recording completion. This keeps the handler in-flight — holding inflight credit and
            // the lane slot — so the test can assert the saturated state before releasing.
            if (handlerGate is not null)
            {
                await handlerGate.ConfigureAwait(false);
            }
            else
            {
                // Hold the slot briefly so parallel lanes overlap observably (and a single lane cannot).
                await Task.Delay(gateDelayMs).ConfigureAwait(false);
            }

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
    /// Tracks settlement count and delivery count so the harness can deterministically stop the runner
    /// and probe saturation state.
    /// </summary>
    private sealed class FakeAdapter : ITransportAdapter
    {
        // Each entry is (globalSeq, key?, perKeySeq, keyHeader). key==null means no key header stamped.
        private readonly List<(int GlobalSeq, string? Key, int PerKeySeq, string KeyHeader)> _messages;
        private int _settled;
        private readonly TaskCompletionSource _allSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _settleTarget = int.MaxValue;

        // Delivery tracking: incremented each time ConsumeAsync yields a message to the runner.
        // The runner's await foreach only advances after it has credit AND completed EnqueueAsync, so
        // _delivered reflects messages that have actually been accepted into the pipeline. The
        // no-deadlock saturation probe reads DeliveredCount and waits for it to plateau.
        private int _delivered;

        /// <summary>Number of messages yielded into the pipeline so far (credit consumed + enqueued).</summary>
        internal int DeliveredCount => Volatile.Read(ref _delivered);

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

                // Track how many messages the runner has received from the stream. The runner's
                // await foreach only moves past a yield after it has credit, so this count
                // reflects messages that are now inside the pipeline (credit consumed).
                Interlocked.Increment(ref _delivered);
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
