using System.Buffers;
using BenchmarkDotNet.Attributes;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;

namespace BareWire.Benchmarks;

/// <summary>
/// Benchmarks for consume-side throughput of the BareWire core pipeline, in isolation from any
/// transport engine: bounded-channel dequeue + settlement acknowledgement against a local sink fake
/// (<see cref="CoreOnlyTransportAdapter"/>).
/// </summary>
/// <remarks>
/// <para>
/// Performance target (Core-only): <c>ConsumeAndAck_CoreOnly</c>: &gt; 300K msgs/s, &lt; 512 B/op.
/// </para>
/// <para>
/// This benchmark measures the core pipeline only. Transport-engine cost (routing, buffer pooling,
/// queue occupancy) is measured separately, and on its own gate, by
/// <see cref="InMemoryTransportBenchmarks.Consume_SingleBinding"/>; the two numbers are not directly
/// comparable — <c>ConsumeAndAck_CoreOnly</c> reports its <c>OperationsPerInvoke</c> per message and
/// never returns a pooled buffer (the Core-only messages below are built without one), while the
/// in-memory benchmark disposes a real pooled buffer per message.
/// </para>
/// NOTE: [EventPipeProfiler] is intentionally omitted — BenchmarkDotNet has a known bug with
/// .NET 10 where runtime detection treats it as v1 (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// Add [EventPipeProfiler] after BenchmarkDotNet ships a fix.
/// </remarks>
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 15)]
[MemoryDiagnoser(displayGenColumns: true)]
public class ConsumeBenchmarks
{
    private const int MessageCount = 1_000;
    private const string EndpointName = "bench-consume";

    private readonly FlowControlOptions _flowControl = new() { InternalQueueCapacity = MessageCount * 2 };

    // Pre-built batch of inbound messages reused across iterations to avoid allocation noise in
    // iteration setup (the batch itself is not part of the measured path). Built WITHOUT a pooled
    // buffer, so re-enqueueing the same instances every iteration and never disposing them afterwards
    // is safe — there is no ArrayPool rental for this Core-only fake to return.
    private InboundMessage[] _batch = null!;
    private CoreOnlyTransportAdapter _adapter = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Build a fixed batch of messages once. Payload is a representative ~100 B JSON blob; matches
        // the shape of BenchmarkMessage.
        var payload = new ReadOnlySequence<byte>(
            System.Text.Encoding.UTF8.GetBytes(
                """{"Id":"order-bench-001","Amount":99.99,"Currency":"USD"}"""));

        var batch = new InboundMessage[MessageCount];
        for (int i = 0; i < MessageCount; i++)
        {
            batch[i] = new InboundMessage(
                messageId: $"bench-consume-{i}",
                headers: new Dictionary<string, string>(),
                body: payload,
                deliveryTag: (ulong)i);
        }

        _batch = batch;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _adapter = new CoreOnlyTransportAdapter(consumeCapacity: MessageCount);
        foreach (InboundMessage message in _batch)
        {
            if (!_adapter.TryEnqueue(message))
            {
                throw new InvalidOperationException(
                    $"CoreOnlyTransportAdapter rejected enqueue of message '{message.MessageId}' — the " +
                    "consume channel capacity must be at least MessageCount.");
            }
        }
    }

    /// <summary>
    /// Consumes all pre-enqueued messages from the Core-only sink adapter and acknowledges each one.
    /// Measures the bounded-channel dequeue + settlement path in isolation from any transport engine.
    /// Target: &gt; 300K msgs/s, &lt; 512 B/op (Core-only).
    /// </summary>
    [Benchmark(OperationsPerInvoke = MessageCount)]
    public async Task ConsumeAndAck_CoreOnly()
    {
        // A deadline so a regression in the consume path fails the benchmark instead of hanging it.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
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
}
