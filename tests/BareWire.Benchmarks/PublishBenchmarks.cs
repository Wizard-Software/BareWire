using BenchmarkDotNet.Attributes;

namespace BareWire.Benchmarks;

/// <summary>
/// Benchmarks for publish-side throughput of the BareWire core pipeline, in isolation from any
/// transport engine: outbound serialization boundary + flow-controlled channel enqueue against a local
/// sink fake (<see cref="CoreOnlyTransportAdapter"/>, via <see cref="CoreOnlyBus"/>).
/// </summary>
/// <remarks>
/// <para>
/// Performance targets (Core-only):
/// <list type="bullet">
/// <item><description>PublishTyped: &gt; 500K msgs/s, &lt; 768 B/msg</description></item>
/// <item><description>PublishRaw: &gt; 1M msgs/s, &lt; 512 B/msg</description></item>
/// </list>
/// </para>
/// <para>
/// These numbers measure the core pipeline only — the transport underneath is a local sink fake that
/// confirms every message without any routing, buffer pooling, or queue bookkeeping. Transport-engine
/// cost is measured separately, and on its own gate, by
/// <see cref="InMemoryTransportBenchmarks.Publish_SingleBinding"/>; the two numbers are not subtracted
/// from one another, because <c>PublishTyped</c> is a steady-state single-call shape while
/// <c>Publish_SingleBinding</c> additionally waits for a queue-occupancy barrier inside its measured
/// window — see that class's remarks.
/// </para>
/// NOTE: [EventPipeProfiler] is intentionally omitted — BenchmarkDotNet has a known bug with
/// .NET 10 where runtime detection treats it as v1 (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// Add [EventPipeProfiler] after BenchmarkDotNet ships a fix.
/// </remarks>
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 15)]
[MemoryDiagnoser(displayGenColumns: true)]
public class PublishBenchmarks
{
    private CoreOnlyBus _core = null!;
    private ReadOnlyMemory<byte> _rawPayload;

    private static readonly BenchmarkMessage _typedMessage = new(
        Id: "order-bench-001",
        Amount: 99.99m,
        Currency: "USD");

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _core = await CoreOnlyBus.StartAsync().ConfigureAwait(false);

        // Pre-create representative JSON payload (~100 B) to avoid allocation in the hot path.
        // Matches the shape of BenchmarkMessage so raw benchmarks measure the same data volume.
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            """{"Id":"order-bench-001","Amount":99.99,"Currency":"USD"}""");
        _rawPayload = new ReadOnlyMemory<byte>(payload);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
        => await _core.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Publishes a typed <see cref="BenchmarkMessage"/> through the core pipeline against the local sink
    /// fake, which accepts every message synchronously after channel enqueue.
    /// Target: &gt; 500K msgs/s, &lt; 768 B/msg (Core-only).
    /// </summary>
    [Benchmark]
    public Task PublishTyped()
        => _core.Bus.PublishAsync(_typedMessage);

    /// <summary>
    /// Publishes a pre-serialized raw payload, bypassing typed serialization entirely.
    /// Measures pure channel + core-pipeline overhead with zero per-call allocations.
    /// Target: &gt; 1M msgs/s, &lt; 512 B/msg (Core-only).
    /// </summary>
    [Benchmark]
    public Task PublishRaw()
        => _core.Bus.PublishRawAsync(_rawPayload, contentType: "application/json");
}

/// <summary>
/// Benchmarks for publish-side allocation scaling with payload size, against the Core-only pipeline
/// (see <see cref="PublishBenchmarks"/> remarks for what "Core-only" means and why the transport
/// underneath is a local sink fake rather than any real transport engine).
/// Shows that per-message overhead is ~544 B fixed + payload size (due to serialization
/// boundary copy in <c>MessagePipeline.ProcessOutboundAsync</c>).
/// </summary>
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 15)]
[MemoryDiagnoser(displayGenColumns: true)]
public class PublishPayloadScalingBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int PayloadSizeBytes { get; set; }

    private CoreOnlyBus _core = null!;
    private ReadOnlyMemory<byte> _rawPayload;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _core = await CoreOnlyBus.StartAsync().ConfigureAwait(false);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Build a JSON payload of approximately the target size.
        // Uses a padding field to reach the desired byte count.
        string padding = new('x', Math.Max(0, PayloadSizeBytes - 50));
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"Id":"bench","Amount":1.00,"Currency":"USD","Pad":"{{padding}}"}""");
        _rawPayload = new ReadOnlyMemory<byte>(payload);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
        => await _core.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Publishes a raw payload of varying size to show allocation scaling.
    /// Fixed overhead (~544 B) + payload size (Core-only).
    /// </summary>
    [Benchmark]
    public Task PublishRaw_Scaled()
        => _core.Bus.PublishRawAsync(_rawPayload, contentType: "application/json");
}

/// <summary>
/// A representative message type for publish benchmarks.
/// Approximately 100 B when serialized to JSON.
/// </summary>
public sealed record BenchmarkMessage(string Id, decimal Amount, string Currency);
