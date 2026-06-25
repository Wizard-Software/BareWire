using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.Benchmarks;

/// <summary>
/// Allocation ceiling benchmark for the per-key ordered consume path (ADR-026, R8.15).
///
/// <para><strong>Goals (PERF-1 / D2 resolution):</strong></para>
/// <list type="bullet">
/// <item><description>
/// <c>OrderedBy_On</c>: allocation per message &lt; 512 B/op on the ordered dispatch path (ADR-003).
/// </description></item>
/// <item><description>
/// Per-lane overhead is CONSTANT (intercept O(L), not O(N×L)): the Allocated/op column should be
/// flat across <see cref="MessageCount"/> values for a fixed <see cref="LaneCount"/>.
/// A rising Allocated/op as N grows indicates per-message overhead — a violation of ADR-003.
/// </description></item>
/// <item><description>
/// <c>Baseline_Off</c> provides the sequential-pump baseline for comparison.
/// </description></item>
/// </list>
/// <para>
/// The <see cref="MessageCount"/> × <see cref="LaneCount"/> params sweep makes the claim
/// derivable from the BenchmarkDotNet output table: for each L column, Allocated/op should be
/// approximately constant across N rows (slope ≈ 0 beyond measurement noise).
/// </para>
/// <para>
/// No new byte[] per message (ADR-003): verify Gen0 stays close to the baseline across the sweep.
/// </para>
///
/// <para><strong>Throughput-floor (SAC vs consistent-hash, ADR-026 §8) — DEFERRED to R8.16:</strong></para>
/// <para>
/// The X% throughput advantage and K minimum absolute throughput for ordered vs baseline are deferred.
/// No acceptance threshold is defined in this package. See R8.16 (ADR-026 §8) for the throughput-floor
/// benchmark and its acceptance criteria. The skeleton method below is intentionally commented out.
/// </para>
/// <code>
/// // THROUGHPUT-FLOOR (SAC vs consistent-hash): X% and K are DEFERRED — see R8.16 (ADR-026 §8).
/// // No acceptance threshold in this package.
/// // [Benchmark]
/// // public async Task ThroughputFloor_SAC_vs_ConsistentHash() { ... }
/// </code>
/// </summary>
[MemoryDiagnoser(displayGenColumns: true)]
#pragma warning disable CA1001 // BenchmarkDotNet lifecycle: disposal is handled by [GlobalCleanup].
public class OrderedConsumeBenchmarks
#pragma warning restore CA1001
{
    private const string EndpointName = "bench-ordered-consume";
    private const string KeyHeader = "x-ordering-key";

    // Params sweep: N (message count) × L (lane count) — enables PERF-1 derivation.
    // Allocated/op should be flat across N for a fixed L: slope ≈ 0 = per-lane constant overhead.
    [Params(500, 2000)]
    public int MessageCount { get; set; }

    [Params(1, 4, 8)]
    public int LaneCount { get; set; }

    // Pre-built message list: (key, perKeySeq) pairs — allocation-neutral at iteration time.
    private List<(string Key, int PerKeySeq)> _messages = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Build a deterministic, key-stamped message list once.
        // 8 unique keys round-robined across MessageCount — exercises fixed-lane hashing.
        const int keyCount = 8;
        _messages = new List<(string Key, int PerKeySeq)>(MessageCount);
        var perKeyCounters = new Dictionary<string, int>(keyCount);

        for (int i = 0; i < MessageCount; i++)
        {
            string key = $"bench-key-{i % keyCount}";
            int seq = perKeyCounters.GetValueOrDefault(key, 0);
            perKeyCounters[key] = seq + 1;
            _messages.Add((key, seq));
        }
    }

    /// <summary>
    /// Ordered consume path: <c>EndpointBinding.Ordering</c> is non-null → OrderedDispatchStage engaged.
    /// Target: &lt; 512 B/op, per-lane overhead constant (not per-msg) across the N×L sweep (ADR-003).
    /// </summary>
    [Benchmark]
    public async Task OrderedBy_On()
    {
        var ordering = new BenchmarkOrdering(headerName: KeyHeader, concurrency: LaneCount);
        var binding = BuildBinding(ordering, LaneCount);
        await RunAsync(binding).ConfigureAwait(false);
    }

    /// <summary>
    /// Baseline (ordering OFF): sequential pump — <c>EndpointBinding.Ordering</c> is null.
    /// Provides the allocation baseline for comparison with <see cref="OrderedBy_On"/>.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task Baseline_Off()
    {
        var binding = BuildBinding(ordering: null, laneCount: LaneCount);
        await RunAsync(binding).ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task RunAsync(EndpointBinding binding)
    {
        int messageCount = _messages.Count;
        var adapter = new BenchmarkFakeAdapter(_messages);
        var completions = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddSingleton(new CompletionTracker(messageCount, completions));
        services.AddScoped<BenchmarkRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var runner = new ReceiveEndpointRunner(
            binding,
            adapter,
            new BenchmarkDeserializerResolver(),
            new BenchmarkPublishEndpoint(),
            new BenchmarkSendEndpointProvider(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FlowController(NullLogger<FlowController>.Instance),
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task runTask = runner.RunAsync(cts.Token);

        // Wait for all messages to complete, then cancel the runner.
        await completions.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        await adapter.SettledAsync(messageCount, cts.Token).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation stops the consume loop.
        }

        await provider.DisposeAsync().ConfigureAwait(false);
    }

    private static EndpointBinding BuildBinding(
        IConsumerOrderingConfiguration? ordering,
        int laneCount)
        => new()
        {
            EndpointName = EndpointName,
            PrefetchCount = 64,
            ConcurrentMessageLimit = laneCount,
            Ordering = ordering,
            RawConsumers = [typeof(BenchmarkRawConsumer)],
        };

    // ── Lightweight ordering config (no NSubstitute — real minimal class) ────

    /// <summary>
    /// Minimal <see cref="IConsumerOrderingConfiguration"/> implementation for the benchmark.
    /// Mirrors the private <c>TestOrdering</c> in ReceiveEndpointRunnerOrderingTests but lives
    /// here so the benchmark has no NSubstitute dependency (PERF-2 requirement).
    /// </summary>
    private sealed class BenchmarkOrdering(string headerName, int concurrency)
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

    // ── Completion tracking (no ConcurrentQueue — minimise allocation noise) ─

    /// <summary>Tracks message completions and signals when the target count is reached.</summary>
    private sealed class CompletionTracker(int target, TaskCompletionSource tcs)
    {
        private int _count;

        internal void Record()
        {
            if (Interlocked.Increment(ref _count) >= target)
            {
                tcs.TrySetResult();
            }
        }
    }

    // ── Raw consumer (no-op: records completion only) ─────────────────────────

    /// <summary>
    /// No-op raw consumer for the benchmark. Resolves <see cref="CompletionTracker"/> from
    /// the DI scope and increments the counter. No allocations beyond DI scope resolution.
    /// </summary>
    private sealed class BenchmarkRawConsumer(CompletionTracker tracker) : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context)
        {
            tracker.Record();
            return Task.CompletedTask;
        }
    }

    // ── Fake transport adapter (mirrors FakeAdapter in ordering unit tests) ───

    /// <summary>
    /// Lightweight fake transport adapter that yields pre-built key-stamped messages.
    /// No NSubstitute — real implementation (PERF-2 requirement). Tracks settlement count
    /// so the benchmark can drain the runner before cancellation.
    /// </summary>
    private sealed class BenchmarkFakeAdapter(IReadOnlyList<(string Key, int PerKeySeq)> messages)
        : ITransportAdapter
    {
        private int _settled;
        private readonly TaskCompletionSource _allSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _settleTarget = int.MaxValue;

        public string TransportName => "BenchmarkFake";
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

            // Block until cancelled so RunAsync stays alive through settlement.
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

    // ── Stub collaborators (no NSubstitute — PERF-2 requirement) ─────────────

    /// <summary>No-op deserializer resolver: returns a trivial deserializer for any content type.</summary>
    private sealed class BenchmarkDeserializerResolver : IDeserializerResolver
    {
        private static readonly BenchmarkMessageDeserializer _instance = new();

        public IMessageDeserializer Resolve(string? contentType) => _instance;
    }

    /// <summary>No-op deserializer: returns null for all inputs (raw consumer path never calls it).</summary>
    private sealed class BenchmarkMessageDeserializer : IMessageDeserializer
    {
        public string ContentType => "application/octet-stream";

        public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class => null;
    }

    /// <summary>No-op publish endpoint: drops all publishes silently.</summary>
    private sealed class BenchmarkPublishEndpoint : IPublishEndpoint
    {
        public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
            where T : class
            => Task.CompletedTask;

        public Task PublishAsync<T>(
            T message,
            IReadOnlyDictionary<string, string>? headers,
            CancellationToken cancellationToken = default)
            where T : class
            => Task.CompletedTask;

        public Task PublishRawAsync(
            ReadOnlyMemory<byte> payload,
            string contentType,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>No-op send endpoint provider: throws if called (ordering path never sends).</summary>
    private sealed class BenchmarkSendEndpointProvider : ISendEndpointProvider
    {
        public Task<ISendEndpoint> GetSendEndpoint(Uri address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("BenchmarkSendEndpointProvider: send endpoint not used in consume benchmark.");
    }
}
