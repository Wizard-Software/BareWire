using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

/// <summary>Minimal JSON message used in the memory stability test (~60 bytes serialised).</summary>
public sealed record MemoryStabilityProbe(string Id, int Iteration);

/// <summary>
/// E2E memory allocation stability tests: verifies that N iterations of
/// publish → consume → Ack → Dispose do not produce monotonically growing allocations,
/// signalling the absence of an unbounded memory leak in the adapter's hot-path.
///
/// <para>
/// Measurement methodology:
/// <list type="bullet">
///   <item>
///     Warmup (≥50 iterations) before measurement — allows JIT, tiered compilation, and the
///     <c>ArrayPool&lt;byte&gt;</c> to reach a steady state before the baseline measurement.
///   </item>
///   <item>
///     Metric: <c>GC.GetTotalAllocatedBytes(precise: true)</c> process-wide — captures
///     allocations from all threads (including the RabbitMQ.Client dispatch thread).
///     We do NOT use <c>GetAllocatedBytesForCurrentThread</c>, which misses allocations
///     on broker dispatcher threads.
///   </item>
///   <item>
///     Relative assertion: allocation of the second half ≤ 2.5× allocation of the first half.
///     Detects gross/monotonic leaks. A hard B/op budget is NOT asserted here
///     (non-falsifiable under a shared Aspire host).
///   </item>
///   <item>
///     <c>GC.Collect() / WaitForPendingFinalizers()</c> before each measurement window
///     minimises noise accumulated between iterations.
///   </item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class MemoryStabilityTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Number of warmup iterations before measurement (required per PERF-1).</summary>
    private const int WarmupIterations = 50;

    /// <summary>Number of iterations in each measurement window.</summary>
    private const int MeasuredIterationsPerWindow = 1_000;

    /// <summary>
    /// Tolerance margin: window-2 allocation ≤ 2.5× window-1 allocation.
    /// The 2.5× factor (per PERF-2) provides resilience against Aspire host noise and JIT warmup
    /// while remaining sensitive to gross/monotonic leaks.
    /// </summary>
    private const double LeakToleranceMultiplier = 2.5;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static async Task<(string ExchangeName, string QueueName)> DeploySimpleTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-mem-ex-{suffix}";
        string queueName = $"e2e-mem-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

    private static FlowControlOptions StandardFlow() =>
        new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

    private static async Task<InboundMessage> ConsumeOneAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, StandardFlow(), ct))
        {
            return msg;
        }

        throw new InvalidOperationException("The consumption stream ended before a message was delivered.");
    }

    /// <summary>
    /// Executes one iteration of publish → consume → Ack → Dispose.
    /// Returns the buffer to the pool via explicit <c>Dispose</c> after settlement (D-3).
    /// </summary>
    private static async Task RunSingleIterationAsync(
        RabbitMqTransportAdapter adapter,
        string exchangeName,
        string queueName,
        int iteration,
        CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new MemoryStabilityProbe(Id: "mem-probe", Iteration: iteration));

        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
            },
            body: body,
            contentType: "application/json");

        await adapter.SendBatchAsync([outbound], ct);
        InboundMessage received = await ConsumeOneAsync(adapter, queueName, ct);
        await adapter.SettleAsync(SettlementAction.Ack, received, ct);
        received.Dispose(); // D-3: explicit Dispose — returns buffer to ArrayPool
    }

    // ── E2E: Memory allocation stability ─────────────────────────────────────

    /// <summary>
    /// Verifies that the publish → consume → Ack → Dispose path does not produce monotonically
    /// growing memory allocations, signalling the absence of an unbounded leak in the RabbitMQ adapter.
    ///
    /// <para>
    /// Assertion: allocation of the second half of iterations (window 2) ≤ 2.5× allocation of the
    /// first half (window 1). The 2.5× threshold is intentionally liberal to absorb shared Aspire
    /// host noise while still detecting gross linear/monotonic leaks.
    /// </para>
    ///
    /// <para>
    /// The test skips deterministically (<see cref="Assert.Skip"/>) if window 1 yielded ~0 bytes
    /// of allocation (which would be measurement noise) — rather than dividing by zero or asserting
    /// against noise.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MemoryAllocations_NIterations_DoNotGrowMonotonically()
    {
        // 180 s: warmup (50 iter) + 2×1000 round-trip iterations against a live broker
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(180));

        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeploySimpleTopologyAsync(adapter, suffix, cts.Token);

        // ── Warmup: JIT, tiered compilation, ArrayPool fill ──────────────────

        for (int i = 0; i < WarmupIterations; i++)
        {
            await RunSingleIterationAsync(adapter, exchangeName, queueName, iteration: i, cts.Token);
        }

        // ── Measurement window 1 ─────────────────────────────────────────────

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocBefore1 = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < MeasuredIterationsPerWindow; i++)
        {
            await RunSingleIterationAsync(adapter, exchangeName, queueName, iteration: WarmupIterations + i, cts.Token);
        }

        long allocAfter1 = GC.GetTotalAllocatedBytes(precise: true);
        long window1Delta = allocAfter1 - allocBefore1;

        // ── Measurement window 2 ─────────────────────────────────────────────

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocBefore2 = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < MeasuredIterationsPerWindow; i++)
        {
            await RunSingleIterationAsync(
                adapter, exchangeName, queueName,
                iteration: WarmupIterations + MeasuredIterationsPerWindow + i,
                cts.Token);
        }

        long allocAfter2 = GC.GetTotalAllocatedBytes(precise: true);
        long window2Delta = allocAfter2 - allocBefore2;

        // ── Assertion ────────────────────────────────────────────────────────

        // If window 1 yielded ~0 (measurement noise below counter resolution),
        // we cannot meaningfully compute a threshold — skip deterministically.
        const long MinMeaningfulDeltaBytes = 4096; // 4 KB: minimum meaningful allocation
        if (window1Delta < MinMeaningfulDeltaBytes)
        {
            Assert.Skip(
                $"Window-1 allocation ({window1Delta} B) is below the measurement noise threshold " +
                $"({MinMeaningfulDeltaBytes} B). Process-wide GC.GetTotalAllocatedBytes " +
                "may be too noisy under the Aspire host to produce a meaningful result. " +
                "Skipping deterministically — never silently green.");
            return;
        }

        long threshold = (long)(window1Delta * LeakToleranceMultiplier);

        window2Delta.Should().BeLessThanOrEqualTo(threshold,
            because:
                $"window 2 ({MeasuredIterationsPerWindow} iter, {window2Delta:N0} B) " +
                $"must not exceed {LeakToleranceMultiplier}× the allocation of window 1 " +
                $"({MeasuredIterationsPerWindow} iter, {window1Delta:N0} B, threshold: {threshold:N0} B). " +
                "Exceeding this signals unbounded/monotonic allocation growth in the " +
                "publish→consume→Ack→Dispose path of the RabbitMQ adapter, indicating a memory leak. " +
                "The hard B/op budget is verified by the isolated BenchmarkDotNet benchmark.");
    }
}
