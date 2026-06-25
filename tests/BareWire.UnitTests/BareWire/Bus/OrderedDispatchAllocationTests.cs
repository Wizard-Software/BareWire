using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// R8.15 — deterministic CI gate for ordered-dispatch allocation regression (ADR-026 §8).
///
/// <para>
/// ADR-026 §8 invariant: the <c>OrderedBy</c> path must add <b>zero per-message allocation</b>
/// beyond the unordered baseline. Constant per-lane structures (bounded channels, lane-state
/// objects) are explicitly allowed as a fixed O(L) overhead — they are <em>not</em> per-message.
/// </para>
///
/// <para><strong>What this gate measures (DELTA, not absolute):</strong></para>
/// <para>
/// Both paths are measured using the slope method (two batch sizes N1 and N2, process-wide
/// <see cref="GC.GetTotalAllocatedBytes"/> counter). The slope isolates the <em>marginal</em>
/// per-message cost and cancels constant infrastructure overhead (DI scope construction,
/// lane channels, runner startup). The gate asserts that:
/// <c>orderedMarginal − unorderedMarginal &lt; tolerance</c>
/// where <c>tolerance = 256 B/msg</c> (measurement noise on a process-wide counter,
/// documented below).
/// </para>
///
/// <para>
/// The shared per-message cost — <c>AsyncServiceScope</c>, <c>MessageContext</c>,
/// DI scope resolution (~3 350 B/msg measured 2026-06-25) — appears in BOTH slopes and
/// cancels in the subtraction. What remains is the ordered-only per-message overhead:
/// ideally zero (lane-state is constant, not per-msg). If the delta is large, that is a
/// real regression that must be fixed in <c>src/</c>.
/// </para>
///
/// <para><strong>Tolerance rationale (256 B/msg):</strong></para>
/// <para>
/// <c>GetTotalAllocatedBytes(precise:true)</c> captures all threads but includes timing-
/// dependent noise from GC thread-local buffers, finalizer threads, and ThreadPool
/// bookkeeping that run between the two batch measurements. 256 B/msg is deliberately
/// large enough to absorb this noise while being tight enough to catch real regressions
/// (e.g. a per-message <c>new byte[256]</c> injection raises the delta to ~256 B/msg,
/// sitting exactly at the tolerance boundary — verified in the falsification below).
/// </para>
///
/// <para><strong>Falsification proof (ADR-026 §8 D2 / PERF-1):</strong></para>
/// <para>
/// <see cref="OrderedDispatch_PerMessageAllocation_NoRegressionVsUnorderedPath_Falsification"/>
/// injects a non-elidable per-message allocation into the <em>ordered</em> handler only
/// (accumulated list so the JIT cannot dead-code-eliminate it) and verifies the gate fails.
/// The unordered handler is unchanged. This proves the measurement is live (not vacuous).
/// </para>
/// </summary>
public sealed class OrderedDispatchAllocationTests
{
    private const string KeyHeader = "x-ordering-key";

    // Slope method constants — shared by both ordered and unordered measurements.
    private const int LaneCount = 4;
    private const int KeyCount = 8;
    private const int WarmupCount = 500;
    private const int SmallCount = 1_000;
    private const int LargeCount = 2_000;

    // ADR-026 §8: delta tolerance for measurement noise.
    //
    // Release (production JIT): 256 B/msg. Large enough to absorb process-wide counter noise
    // (GC thread-local buffers, finalizer threads, ThreadPool bookkeeping between batches);
    // tight enough to catch real regressions — a per-message new byte[256] injection raises
    // the delta to ~256 B/msg, sitting at the boundary (see falsification test).
    //
    // Debug (unoptimized): 640 B/msg. In Debug builds the JIT does NOT promote async state
    // machines to stack frames, so EnqueueAsync (ordered path) and WriteAsync<WorkItem>
    // allocate their state machines on the heap per invocation. These are structural
    // Debug-only allocations (~486 B/msg measured 2026-06-25) that vanish in Release
    // (JIT devirtualization + async-state-machine stack promotion). They are NOT production
    // regressions — the ADR-026 §8 invariant applies to the Release (JIT-optimized)
    // configuration. The Debug tolerance covers the known ~486 B/msg overhead plus 256 B
    // noise headroom, rounded up to 640 B/msg.
    // Falsification in Debug uses the wider tolerance to remain sensitive: injecting
    // new byte[256] per message must still push the delta above this wider ceiling.
#if DEBUG
    private const long DeltaToleranceBytesPerMessage = 640L;
#else
    private const long DeltaToleranceBytesPerMessage = 256L;
#endif

    /// <summary>
    /// ADR-026 §8 allocation gate: the ordered path must not add per-message allocation
    /// beyond the unordered baseline (slope-delta method, process-wide counter).
    ///
    /// <para>
    /// Both paths are exercised with the <b>same</b> slope method (two batch sizes, GC fence
    /// between each). The constant per-lane overhead of the ordered path (channel objects,
    /// lane-state) cancels in the slope subtraction and does NOT appear in the delta.
    /// </para>
    ///
    /// <para>
    /// Pass condition: <c>orderedMarginal − unorderedMarginal &lt; 256 B/msg</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OrderedDispatch_PerMessageAllocation_NoRegressionVsUnorderedPath()
    {
        var ordering = new GateOrdering(headerName: KeyHeader, concurrency: LaneCount);

        // Warmup both paths: JIT all branches, settle ArrayPool caches.
        await RunRunnerCoreAsync(ordering, LaneCount, BuildMessages(WarmupCount, KeyCount));
        await RunRunnerCoreAsync(null, LaneCount, BuildMessages(WarmupCount, KeyCount));

        // ── Ordered path — slope measurement ─────────────────────────────────

        long orderedMarginal = await MeasureMarginalAsync(ordering);

        // ── Unordered path — slope measurement ───────────────────────────────

        long unorderedMarginal = await MeasureMarginalAsync(null);

        // ── ADR-026 §8 delta assertion ────────────────────────────────────────
        // The ordered path must add zero per-message allocation vs the unordered baseline.
        // delta = orderedMarginal − unorderedMarginal should be ≈ 0 B/msg.
        // Tolerance 256 B/msg absorbs process-wide counter noise (see class XML-doc).
        long delta = orderedMarginal - unorderedMarginal;

        delta.Should().BeLessThan(DeltaToleranceBytesPerMessage,
            $"ADR-026 §8: the ordered path must not add per-message allocation vs the " +
            $"unordered baseline. delta = orderedMarginal ({orderedMarginal} B/msg) − " +
            $"unorderedMarginal ({unorderedMarginal} B/msg) = {delta} B/msg. " +
            $"Tolerance: {DeltaToleranceBytesPerMessage} B/msg (measurement noise). " +
            $"A regression in the ordered path has introduced per-message allocation. " +
            $"Check for: boxing, closure captures, new byte[] per-msg in the ordered-dispatch " +
            $"branch (ADR-003), or lane-state growth proportional to message count.");
    }

    /// <summary>
    /// Falsification: injecting a per-message non-elidable allocation into the ORDERED
    /// handler only raises the delta beyond the tolerance — proving the gate is live.
    ///
    /// <para>
    /// The per-message allocation is accumulated into a <see cref="List{T}"/> held on
    /// <see cref="AllocationRawConsumer"/> (via <see cref="AllocationCompletionTracker"/>)
    /// so the JIT cannot dead-code-eliminate the allocation even in Release builds.
    /// </para>
    ///
    /// <para>
    /// Expected outcome: <c>delta &gt;= tolerance</c> — gate FAILS (inverted assertion).
    /// If this test fails, it means the gate is vacuous (cannot detect regressions).
    /// </para>
    /// </summary>
    [Fact]
    public async Task OrderedDispatch_PerMessageAllocation_NoRegressionVsUnorderedPath_Falsification()
    {
        var ordering = new GateOrdering(headerName: KeyHeader, concurrency: LaneCount);

        // Warmup both paths.
        await RunRunnerCoreAsync(ordering, LaneCount, BuildMessages(WarmupCount, KeyCount),
            injectPerMsgAlloc: false);
        await RunRunnerCoreAsync(null, LaneCount, BuildMessages(WarmupCount, KeyCount),
            injectPerMsgAlloc: false);

        // Ordered path with per-msg injection (new byte[256] accumulated per message).
        long orderedMarginalWithInjection = await MeasureMarginalAsync(ordering, injectPerMsgAlloc: true);

        // Unordered path — no injection (baseline unchanged).
        long unorderedMarginal = await MeasureMarginalAsync(null, injectPerMsgAlloc: false);

        long deltaWithInjection = orderedMarginalWithInjection - unorderedMarginal;

        // Falsification: delta MUST exceed tolerance — gate must fail when regression exists.
        deltaWithInjection.Should().BeGreaterThanOrEqualTo(DeltaToleranceBytesPerMessage,
            $"Falsification: injecting new byte[256] per ordered-path message must raise the " +
            $"delta to >= {DeltaToleranceBytesPerMessage} B/msg, proving the gate is live. " +
            $"orderedMarginal (with injection) = {orderedMarginalWithInjection} B/msg; " +
            $"unorderedMarginal (no injection) = {unorderedMarginal} B/msg; " +
            $"delta = {deltaWithInjection} B/msg. " +
            $"If this assertion fails, the gate is vacuous (cannot detect real regressions).");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Measures marginal per-message allocation via the two-point slope method
    /// using the process-wide <see cref="GC.GetTotalAllocatedBytes"/> counter.
    ///
    /// <para>
    /// slope = (delta_N2 − delta_N1) / (N2 − N1) cancels constant overhead
    /// (DI scope construction, lane startup, channel objects) — only per-message
    /// marginal cost survives.
    /// </para>
    /// </summary>
    private static async Task<long> MeasureMarginalAsync(
        IConsumerOrderingConfiguration? ordering,
        bool injectPerMsgAlloc = false)
    {
        var smallMessages = BuildMessages(SmallCount, KeyCount);

        GcFence();
        long beforeSmall = GC.GetTotalAllocatedBytes(precise: true);
        await RunRunnerCoreAsync(ordering, LaneCount, smallMessages, injectPerMsgAlloc);
        GcFence();
        long delta1 = GC.GetTotalAllocatedBytes(precise: true) - beforeSmall;

        var largeMessages = BuildMessages(LargeCount, KeyCount);

        GcFence();
        long beforeLarge = GC.GetTotalAllocatedBytes(precise: true);
        await RunRunnerCoreAsync(ordering, LaneCount, largeMessages, injectPerMsgAlloc);
        GcFence();
        long delta2 = GC.GetTotalAllocatedBytes(precise: true) - beforeLarge;

        return (delta2 - delta1) / (LargeCount - SmallCount);
    }

    private static void GcFence()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    private static List<(string Key, int PerKeySeq)> BuildMessages(int count, int keyCount)
    {
        var messages = new List<(string Key, int PerKeySeq)>(count);
        var perKeyCounters = new Dictionary<string, int>(keyCount);

        for (int i = 0; i < count; i++)
        {
            string key = $"alloc-key-{i % keyCount}";
            int seq = perKeyCounters.GetValueOrDefault(key, 0);
            perKeyCounters[key] = seq + 1;
            messages.Add((key, seq));
        }

        return messages;
    }

    private static async Task RunRunnerCoreAsync(
        IConsumerOrderingConfiguration? ordering,
        int laneCount,
        List<(string Key, int PerKeySeq)> messages,
        bool injectPerMsgAlloc = false)
    {
        int messageCount = messages.Count;
        var adapter = new GateFakeAdapter(messages);
        var completions = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new AllocationCompletionTracker(messageCount, completions, injectPerMsgAlloc);

        var services = new ServiceCollection();
        services.AddSingleton(tracker);
        services.AddScoped<AllocationRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        EndpointBinding binding = new()
        {
            EndpointName = "alloc-gate-endpoint",
            PrefetchCount = 64,
            ConcurrentMessageLimit = laneCount,
            Ordering = ordering,
            RawConsumers = [typeof(AllocationRawConsumer)],
        };

        // Hand-rolled stubs — no NSubstitute proxy allocation noise in the measured window.
        var runner = new ReceiveEndpointRunner(
            binding,
            adapter,
            new GateDeserializerResolver(),
            new GatePublishEndpoint(),
            new GateSendEndpointProvider(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FlowController(NullLogger<FlowController>.Instance),
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task runTask = runner.RunAsync(cts.Token);

        await completions.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        await adapter.SettledAsync(messageCount, cts.Token).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        try
        {
            // Await ensures ALL lane worker Tasks have completed and their allocations
            // are committed to the GC heap before control returns to MeasureMarginalAsync.
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation stops the consume loop.
        }

        await provider.DisposeAsync().ConfigureAwait(false);
    }

    // ── Test-local helpers ────────────────────────────────────────────────────

    /// <summary>Minimal ordering config for the allocation gate test.</summary>
    private sealed class GateOrdering(string headerName, int concurrency)
        : IConsumerOrderingConfiguration
    {
        public string? HeaderName { get; } = headerName;
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency { get; } = concurrency;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public TransportAffinity TransportAffinity => TransportAffinity.None;
        public int MaxDeliveryAttempts => 0;
    }

    /// <summary>
    /// Tracks completions and signals the TCS when the target count is reached.
    /// When <paramref name="injectPerMsgAlloc"/> is <see langword="true"/>, each call to
    /// <see cref="Record"/> allocates a <c>new byte[256]</c> accumulated in
    /// <see cref="_sink"/> so the JIT cannot dead-code-eliminate the allocation
    /// (the array is reachable via the field for the lifetime of the tracker).
    /// </summary>
    private sealed class AllocationCompletionTracker(
        int target,
        TaskCompletionSource tcs,
        bool injectPerMsgAlloc = false)
    {
        private int _count;

        // Non-null only when injectPerMsgAlloc=true. Holds per-message arrays to
        // prevent the JIT from eliding the allocation as dead code.
        private readonly List<byte[]>? _sink = injectPerMsgAlloc ? [] : null;

        internal void Record()
        {
            if (_sink is not null)
            {
                // Non-elidable: array is appended to a list reachable via the tracker,
                // so the JIT cannot treat it as dead code. This simulates a per-message
                // allocation regression on the ordered path.
                _sink.Add(new byte[256]);
            }

            if (Interlocked.Increment(ref _count) >= target)
            {
                tcs.TrySetResult();
            }
        }
    }

    /// <summary>No-op raw consumer: records completion only.</summary>
    private sealed class AllocationRawConsumer(AllocationCompletionTracker tracker) : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context)
        {
            tracker.Record();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake transport adapter for the allocation gate test. Yields key-stamped empty-bodied
    /// messages and tracks settlement so the harness can drain deterministically.
    /// </summary>
    private sealed class GateFakeAdapter(List<(string Key, int PerKeySeq)> messages)
        : ITransportAdapter
    {
        private int _settled;
        private readonly TaskCompletionSource _allSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _settleTarget = int.MaxValue;

        public string TransportName => "GateFake";
        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                (string key, int perKeySeq) = messages[i];
                var headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["seq"] = perKeySeq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [KeyHeader] = key,
                };

                yield return new InboundMessage(
                    messageId: Guid.NewGuid().ToString(),
                    headers: headers,
                    body: ReadOnlySequence<byte>.Empty,
                    deliveryTag: (ulong)i);
            }

            var tcs = new TaskCompletionSource();
            using (cancellationToken.Register(() => tcs.TrySetResult()))
            {
                await tcs.Task.ConfigureAwait(false);
            }

            yield break;
        }

        public Task SettleAsync(
            SettlementAction action,
            InboundMessage message,
            CancellationToken cancellationToken = default)
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
            IReadOnlyList<OutboundMessage> messages,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(
            BareWire.Abstractions.Topology.TopologyDeclaration topology,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>No-op deserializer resolver stub — no NSubstitute proxy allocations.</summary>
    private sealed class GateDeserializerResolver : IDeserializerResolver
    {
        private static readonly GateMessageDeserializer _instance = new();

        public IMessageDeserializer Resolve(string? contentType) => _instance;
    }

    /// <summary>No-op deserializer stub. Raw consumer path never calls Deserialize.</summary>
    private sealed class GateMessageDeserializer : IMessageDeserializer
    {
        public string ContentType => "application/octet-stream";

        public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class => null;
    }

    /// <summary>No-op publish endpoint stub.</summary>
    private sealed class GatePublishEndpoint : IPublishEndpoint
    {
        public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
            where T : class => Task.CompletedTask;

        public Task PublishAsync<T>(
            T message,
            IReadOnlyDictionary<string, string>? headers,
            CancellationToken cancellationToken = default)
            where T : class => Task.CompletedTask;

        public Task PublishRawAsync(
            ReadOnlyMemory<byte> payload,
            string contentType,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>No-op send endpoint provider stub (ordering path never sends).</summary>
    private sealed class GateSendEndpointProvider : ISendEndpointProvider
    {
        public Task<ISendEndpoint> GetSendEndpoint(Uri address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("GateSendEndpointProvider: send not used in consume allocation gate.");
    }
}
