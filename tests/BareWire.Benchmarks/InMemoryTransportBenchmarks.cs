using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.InMemory;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.Benchmarks;

/// <summary>
/// Benchmarks for the real in-memory transport engine on a single exchange/queue binding —
/// unlike <see cref="PublishBenchmarks"/>/<see cref="ConsumeBenchmarks"/>, which measure the core
/// pipeline against a local sink fake, this class runs the actual <c>BareWire.Transport.InMemory</c>
/// adapter: routing, per-message buffer pooling, and queue bookkeeping.
/// </summary>
/// <remarks>
/// <para>
/// Performance targets (transport gate, same numbers as the Core-only budget in <c>CLAUDE.md</c>):
/// <list type="bullet">
/// <item><description>Publish_SingleBinding: &gt; 500K msgs/s, &lt; 768 B/msg</description></item>
/// <item><description>Consume_SingleBinding: &gt; 300K msgs/s, &lt; 512 B/op</description></item>
/// </list>
/// </para>
/// <para>
/// <see cref="Publish_SingleBinding"/> publishes through <c>IBus.PublishAsync</c>, which only enqueues
/// onto the bus's own bounded outbound channel — the transport's background publishing loop drains that
/// channel and calls <c>SendBatchAsync</c> on its own schedule, so a bare <c>PublishAsync</c> loop would
/// measure channel enqueue only and let the transport's real cost happen outside the timed window. To
/// keep that cost inside the measured window, the benchmark method also waits, after publishing, for the
/// destination queue's occupancy to actually reach the published count (a barrier on
/// <c>InMemoryQueue.Occupancy</c>, reached via <c>InternalsVisibleTo</c>), bounded by a 30-second
/// deadline that throws <see cref="TimeoutException"/> instead of silently under-measuring. Because of
/// this barrier, <c>Publish_SingleBinding</c> and the Core-only <c>PublishBenchmarks.PublishTyped</c>
/// measure different shapes of work and are reported against the same targets independently — never
/// subtracted from one another.
/// </para>
/// <para>
/// Both benchmarks use the same <see cref="FixedPayloadMessageSerializer"/> payload as the Core-only
/// baseline (~56 B JSON), so the two baselines are comparable on payload shape even though their
/// pipelines differ.
/// </para>
/// NOTE: [EventPipeProfiler] is intentionally omitted — BenchmarkDotNet has a known bug with
/// .NET 10 where runtime detection treats it as v1 (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// Add [EventPipeProfiler] after BenchmarkDotNet ships a fix.
/// </remarks>
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 15)]
[MemoryDiagnoser(displayGenColumns: true)]
public class InMemoryTransportBenchmarks
{
    private const string QueueName = "bench-inmemory-single";
    private const int QueueCapacity = 1_000;
    private const int PublishBatchSize = 256;
    private const int ConsumeMessageCount = 1_000;

    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    private static readonly BenchmarkMessage _typedMessage = new(
        Id: "order-bench-001",
        Amount: 99.99m,
        Currency: "USD");

    private ServiceProvider _provider = null!;
    private IBusControl _busControl = null!;
    private ITransportAdapter _adapter = null!;
    private InMemoryBroker _broker = null!;
    private FlowControlOptions _flowControl = null!;
    private OutboundMessage[] _consumeFillBatch = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.TryAddSingleton<IMessageSerializer>(new FixedPayloadMessageSerializer());
        services.TryAddSingleton<IMessageDeserializer>(new NoOpMessageDeserializer());

        services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange(string.Empty);
            t.ConfigureTopology(topo => topo.DeclareQueue(QueueName));
            t.QueueCapacity(QueueCapacity);
            t.MapRoutingKey<BenchmarkMessage>(QueueName);
        });

        _provider = services.BuildServiceProvider();
        _busControl = _provider.GetRequiredService<IBusControl>();
        _adapter = _provider.GetRequiredService<ITransportAdapter>();
        _broker = _provider.GetRequiredService<InMemoryBroker>();

        await _busControl.StartAsync().ConfigureAwait(false);

        _flowControl = new FlowControlOptions { InternalQueueCapacity = ConsumeMessageCount * 2 };

        var fillBatch = new OutboundMessage[ConsumeMessageCount];
        for (int i = 0; i < ConsumeMessageCount; i++)
        {
            fillBatch[i] = BuildOutboundMessage();
        }

        _consumeFillBatch = fillBatch;

        // Smoke: prove the binding is wired, then fully drain it. The queue capacity equals the
        // Consume_SingleBinding fill count exactly, so a leftover message here would latch the queue and
        // silently reject the first iteration's fill.
        IReadOnlyList<SendResult> smoke = await _adapter
            .SendBatchAsync([BuildOutboundMessage()])
            .ConfigureAwait(false);
        ThrowIfAnyUnconfirmed(smoke, "smoke send");
        await DrainAsync(_adapter, QueueName, count: 1, _flowControl).ConfigureAwait(false);
        EnsureQueueEmpty("after the setup smoke send");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        try
        {
            await _busControl.StopAsync().WaitAsync(BarrierTimeout).ConfigureAwait(false);
        }
        finally
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    [IterationSetup(Target = nameof(Consume_SingleBinding))]
    public void FillQueueForConsume()
        => FillQueueForConsumeAsync().GetAwaiter().GetResult();

    [IterationCleanup(Target = nameof(Publish_SingleBinding))]
    public void DrainQueueAfterPublish()
    {
        DrainAsync(_adapter, QueueName, PublishBatchSize, _flowControl).GetAwaiter().GetResult();

        // A leftover message would open the next iteration's occupancy barrier early without any error.
        EnsureQueueEmpty("after draining a publish iteration");
    }

    private void EnsureQueueEmpty(string when)
    {
        if (!_broker.TryGetQueue(QueueName, out InMemoryQueue? queue))
        {
            throw new InvalidOperationException($"Queue '{QueueName}' was not found on the in-memory broker.");
        }

        if (queue.Occupancy != 0)
        {
            throw new InvalidOperationException(
                $"Queue '{QueueName}' still holds {queue.Occupancy} message(s) {when}; the publish barrier " +
                "requires an empty queue at the start of every iteration.");
        }
    }

    /// <summary>
    /// Publishes <see cref="PublishBatchSize"/> typed messages through the bus and, still inside the
    /// measured window, waits for the destination queue's occupancy to reach that count — see the class
    /// remarks for why the barrier is required.
    /// Target: &gt; 500K msgs/s, &lt; 768 B/msg.
    /// </summary>
    [Benchmark(OperationsPerInvoke = PublishBatchSize)]
    public async Task Publish_SingleBinding()
    {
        for (int i = 0; i < PublishBatchSize; i++)
        {
            await _busControl.PublishAsync(_typedMessage).ConfigureAwait(false);
        }

        await WaitForQueueOccupancyAsync(PublishBatchSize).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains <see cref="ConsumeMessageCount"/> pre-filled messages from the real in-memory transport
    /// adapter, acknowledging and disposing each one (returning its pooled buffer).
    /// Target: &gt; 300K msgs/s, &lt; 512 B/op.
    /// </summary>
    [Benchmark(OperationsPerInvoke = ConsumeMessageCount)]
    public Task Consume_SingleBinding()
        => DrainAsync(_adapter, QueueName, ConsumeMessageCount, _flowControl);

    /// <summary>
    /// Drains exactly <paramref name="count"/> messages from <paramref name="queueName"/> through
    /// <paramref name="adapter"/>, acknowledging and disposing each one — <see cref="InboundMessage.Dispose"/>
    /// is what returns the transport's pooled buffer, so it is called unconditionally, including when
    /// settlement itself throws. Bounded by <see cref="BarrierTimeout"/>; the wait ends loudly
    /// (<see cref="TimeoutException"/>) rather than silently under-draining.
    /// </summary>
    internal static async Task DrainAsync(
        ITransportAdapter adapter, string queueName, int count, FlowControlOptions flowControl)
    {
        using CancellationTokenSource cts = new(BarrierTimeout);
        int drained = 0;

        try
        {
            await foreach (InboundMessage message in adapter
                .ConsumeAsync(queueName, flowControl, cts.Token)
                .ConfigureAwait(false))
            {
                try
                {
                    await adapter.SettleAsync(SettlementAction.Ack, message).ConfigureAwait(false);
                }
                finally
                {
                    message.Dispose();
                }

                if (++drained >= count)
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && drained < count)
        {
            throw new TimeoutException(
                $"Draining queue '{queueName}' did not observe {count} message(s) within {BarrierTimeout} " +
                $"(drained {drained}).");
        }
    }

    private async Task FillQueueForConsumeAsync()
    {
        IReadOnlyList<SendResult> results = await _adapter
            .SendBatchAsync(_consumeFillBatch)
            .ConfigureAwait(false);
        ThrowIfAnyUnconfirmed(results, "queue fill for Consume_SingleBinding");
    }

    private async Task WaitForQueueOccupancyAsync(int target)
    {
        if (!_broker.TryGetQueue(QueueName, out InMemoryQueue? queue))
        {
            throw new InvalidOperationException($"Queue '{QueueName}' was not found on the in-memory broker.");
        }

        // Timer-free deadline: a timed CancellationTokenSource would allocate inside the timed window.
        long start = Stopwatch.GetTimestamp();
        while (queue.Occupancy < target)
        {
            if (Stopwatch.GetElapsedTime(start) > BarrierTimeout)
            {
                throw new TimeoutException(
                    $"Queue '{QueueName}' did not reach occupancy {target} within {BarrierTimeout} " +
                    $"(observed {queue.Occupancy}).");
            }

            await Task.Yield();
        }
    }

    private static void ThrowIfAnyUnconfirmed(IReadOnlyList<SendResult> results, string operationName)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (!results[i].IsConfirmed)
            {
                throw new InvalidOperationException(
                    $"SendBatchAsync did not confirm message at index {i} during {operationName} — the " +
                    "benchmark would otherwise measure a partially filled queue.");
            }
        }
    }

    private static OutboundMessage BuildOutboundMessage() =>
        new(
            routingKey: QueueName,
            headers: EmptyHeaders,
            body: FixedPayloadMessageSerializer.PayloadBytes,
            contentType: "application/json");
}
