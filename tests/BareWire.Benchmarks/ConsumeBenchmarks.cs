using System.Buffers;
using BenchmarkDotNet.Attributes;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Testing;

namespace BareWire.Benchmarks;

/// <summary>
/// Benchmarks for consume-side throughput through the in-memory transport.
/// Measures the inbound pipeline performance: channel dequeue + settlement acknowledgement.
/// Uses <see cref="InMemoryTransportAdapter"/> directly for precise measurement of the
/// consume + ack path without bus dispatch overhead.
/// </summary>
/// <remarks>
/// Performance targets:
/// <list type="bullet">
/// <item><description>ConsumeAndAck_InMemory: &gt; 300K msgs/s, &lt; 256 B/msg</description></item>
/// </list>
/// NOTE: [EventPipeProfiler] is intentionally omitted — BenchmarkDotNet has a known bug with
/// .NET 10 where runtime detection treats it as v1 (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// Add [EventPipeProfiler] after BenchmarkDotNet ships a fix.
/// </remarks>
[MemoryDiagnoser(displayGenColumns: true)]
#pragma warning disable CA1001 // BenchmarkDotNet lifecycle: disposal is handled by [GlobalCleanup] / [IterationSetup].
public class ConsumeBenchmarks
#pragma warning restore CA1001
{
    private const int MessageCount = 1_000;
    private const string EndpointName = "bench-consume";

    private InMemoryTransportAdapter _adapter = null!;
    private FlowControlOptions _flowControl = null!;

    // Pre-built batch of outbound messages reused across iterations to avoid allocation noise
    // in iteration setup (the batch itself is not part of the measured path).
    private IReadOnlyList<OutboundMessage> _batch = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _flowControl = new FlowControlOptions
        {
            InternalQueueCapacity = MessageCount * 2,
        };

        // Build a fixed batch of pre-serialized messages once.
        // Payload is a representative ~100 B JSON blob; matches the shape of BenchmarkMessage.
        ReadOnlyMemory<byte> payload = new(
            System.Text.Encoding.UTF8.GetBytes(
                """{"Id":"order-bench-001","Amount":99.99,"Currency":"USD"}"""));

        var batch = new OutboundMessage[MessageCount];
        for (int i = 0; i < MessageCount; i++)
        {
            batch[i] = new OutboundMessage(
                routingKey: EndpointName,
                headers: new Dictionary<string, string>(),
                body: payload,
                contentType: "application/json");
        }

        _batch = batch;

        // Create and pre-fill the adapter for the first iteration.
        await CreateAndFillAdapterAsync().ConfigureAwait(false);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Dispose previous adapter (channel must be drained to empty before each iteration).
        _adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        CreateAndFillAdapterAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
        => await _adapter.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Consumes all pre-published messages from the in-memory transport and acknowledges each one.
    /// Measures the bounded-channel dequeue + no-op ack path.
    /// Target: &gt; 300K msgs/s, &lt; 256 B/msg.
    /// </summary>
    [Benchmark]
    public async Task ConsumeAndAck_InMemory()
    {
        using CancellationTokenSource cts = new();
        int consumed = 0;

        await foreach (InboundMessage message in _adapter
            .ConsumeAsync(EndpointName, _flowControl, cts.Token)
            .ConfigureAwait(false))
        {
            await _adapter.SettleAsync(SettlementAction.Ack, message).ConfigureAwait(false);

            if (++consumed >= MessageCount)
            {
                cts.Cancel();
                break;
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task CreateAndFillAdapterAsync()
    {
        _adapter = new InMemoryTransportAdapter();
        await _adapter.SendBatchAsync(_batch).ConfigureAwait(false);
    }
}
