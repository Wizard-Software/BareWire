using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.Benchmarks;

/// <summary>
/// Benchmarks fan-out delivery through the real in-memory transport: one send lands a copy in every
/// bound queue, and a steady-state pool of consumers acknowledges each copy.
/// </summary>
/// <remarks>
/// <para>
/// Adapter-only — no <c>IBus</c> is started (<c>AddBareWireInMemory</c> interprets its topology in the
/// adapter's own constructor, so it works without a running bus). The topology is one fanout exchange
/// bound to <see cref="FanOut"/> queues with an empty routing key.
/// </para>
/// <para>
/// <see cref="FanOut_DeliverAndAck"/> is the only benchmark in this class — a send-only counterpart
/// cannot be measured honestly without artificially holding thousands of unconsumed pooled buffers in
/// flight (far beyond <c>ArrayPool&lt;byte&gt;.Shared</c>'s per-core retention), which would measure pool
/// misses rather than the transport. <see cref="FanOut"/> consumer loops are started once in
/// <see cref="Setup"/> and run for the whole benchmark, acknowledging and disposing every delivered copy
/// as it arrives — the steady state this class measures. The last acknowledging consumer completes the
/// invocation (no poller in the timed window); a long-lived watchdog fails a stalled invocation after
/// 30 s instead of letting it hang.
/// </para>
/// <para>
/// <see cref="BenchmarkDotNet.Attributes.OperationsPerInvokeAttribute"/> is set to
/// <see cref="BatchSize"/> — the number of messages <em>sent</em> per call — so the reported time and
/// allocation are per published message. The budget per <em>delivered copy</em> is that value divided by
/// <see cref="FanOut"/>; the report computes and tabulates that division explicitly, together with the
/// shared-buffer-with-reference-count decision it feeds.
/// </para>
/// NOTE: [EventPipeProfiler] is intentionally omitted — BenchmarkDotNet has a known bug with
/// .NET 10 where runtime detection treats it as v1 (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// Add [EventPipeProfiler] after BenchmarkDotNet ships a fix.
/// </remarks>
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 15)]
[MemoryDiagnoser(displayGenColumns: true)]
#pragma warning disable CA1001 // BenchmarkDotNet lifecycle: disposal is handled by [GlobalCleanup].
public class InMemoryFanOutBenchmarks
#pragma warning restore CA1001
{
    private const string ExchangeName = "bench-fanout";
    private const int QueueCapacity = 256;
    // Small enough that BatchSize x the largest FanOut (16 x 16 = 256 copies) stays within the shared
    // array pool's retention, so per-copy allocation reflects the transport rather than pool misses.
    private const int BatchSize = 16;

    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();

    [Params(1, 4, 16)]
    public int FanOut { get; set; }

    [Params(128, 4_096)]
    public int PayloadBytes { get; set; }

    private ServiceProvider _provider = null!;
    private ITransportAdapter _adapter = null!;
    private FlowControlOptions _flowControl = null!;
    private OutboundMessage[] _batch = null!;
    private CancellationTokenSource _consumerCts = null!;
    private Task[] _consumerLoops = null!;
    private Task _watchdog = null!;
    private long _confirmedCount;
    private TargetWaiter? _targetReached;

    [GlobalSetup]
    public void Setup()
    {
        int fanOut = FanOut;

        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddBareWireInMemory(t =>
        {
            t.DefaultExchange(ExchangeName);
            t.QueueCapacity(QueueCapacity);
            t.ConfigureTopology(topo =>
            {
                topo.DeclareExchange(ExchangeName, ExchangeType.Fanout);
                for (int k = 0; k < fanOut; k++)
                {
                    string queueName = QueueName(k);
                    topo.DeclareQueue(queueName);
                    topo.BindExchangeToQueue(ExchangeName, queueName, string.Empty);
                }
            });
        });

        _provider = services.BuildServiceProvider();
        _adapter = _provider.GetRequiredService<ITransportAdapter>();

        _flowControl = new FlowControlOptions { InternalQueueCapacity = QueueCapacity };
        _batch = BuildBatch(BuildPayload(PayloadBytes), BatchSize);
        _confirmedCount = 0;

        _consumerCts = new CancellationTokenSource();
        _consumerLoops = new Task[fanOut];
        for (int k = 0; k < fanOut; k++)
        {
            string queueName = QueueName(k);
            _consumerLoops[k] = RunConsumerLoopAsync(queueName, _consumerCts.Token);
        }

        _watchdog = RunWatchdogAsync(_consumerCts.Token);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _consumerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll([.. _consumerLoops, _watchdog]).WaitAsync(AckTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: GlobalCleanup cancels every consumer loop and the watchdog.
        }
        finally
        {
            _consumerCts.Dispose();
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Fails the pending invocation loudly when no copy has been acknowledged for AckTimeout while a
    // target is outstanding, so a stalled transport shows up as a failed benchmark instead of a hang.
    private async Task RunWatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            long lastSeen = -1;
            long lastProgress = Stopwatch.GetTimestamp();
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

                long confirmed = Volatile.Read(ref _confirmedCount);
                TargetWaiter? pending = Volatile.Read(ref _targetReached);
                bool outstanding = pending is { Task.IsCompleted: false };
                if (confirmed != lastSeen || !outstanding)
                {
                    lastSeen = confirmed;
                    lastProgress = Stopwatch.GetTimestamp();
                    continue;
                }

                if (Stopwatch.GetElapsedTime(lastProgress) > AckTimeout)
                {
                    pending!.TrySetException(new TimeoutException(
                        $"Fan-out delivery+ack made no progress for {AckTimeout} (confirmed {confirmed}, " +
                        $"target {pending.Target})."));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: CleanupAsync cancels the watchdog at the end of the run.
        }
    }

    /// <summary>
    /// Sends <see cref="BatchSize"/> messages onto the fanout exchange and waits for all
    /// <see cref="FanOut"/> copies of each one to be delivered and acknowledged by the steady-state
    /// consumer loops started in <see cref="Setup"/>.
    /// </summary>
    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task FanOut_DeliverAndAck()
    {
        long before = Volatile.Read(ref _confirmedCount);

        IReadOnlyList<SendResult> results = await _adapter.SendBatchAsync(_batch).ConfigureAwait(false);
        for (int i = 0; i < results.Count; i++)
        {
            if (!results[i].IsConfirmed)
            {
                throw new InvalidOperationException(
                    $"SendBatchAsync did not confirm message at index {i} during fan-out send — the " +
                    "benchmark would measure a partial fan-out.");
            }
        }

        long target = before + ((long)BatchSize * FanOut);

        // Completion is signalled by the consumer that acknowledges the last copy, so no poller competes
        // with the consumers for CPU inside the timed window. The deadline is enforced by the long-lived
        // watchdog started in GlobalSetup (no per-invocation timer allocation).
        // The target travels inside the completion source and both are published by ONE write, so a
        // consumer can never pair a stale target with the new completion source (early completion).
        var targetReached = new TargetWaiter(target);
        Interlocked.Exchange(ref _targetReached, targetReached);

        if (Volatile.Read(ref _confirmedCount) >= target)
        {
            targetReached.TrySetResult();
        }

        await targetReached.Task.ConfigureAwait(false);
    }

    private async Task RunConsumerLoopAsync(string queueName, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (InboundMessage message in _adapter
                .ConsumeAsync(queueName, _flowControl, cancellationToken)
                .ConfigureAwait(false))
            {
                try
                {
                    await _adapter
                        .SettleAsync(SettlementAction.Ack, message, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    message.Dispose();
                }

                long confirmed = Interlocked.Increment(ref _confirmedCount);
                TargetWaiter? waiter = Volatile.Read(ref _targetReached);
                if (waiter is not null && confirmed >= waiter.Target)
                {
                    waiter.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: CleanupAsync cancels every consumer loop at the end of the run.
        }
    }

    private static string QueueName(int index) => $"{ExchangeName}-{index}";

    private static ReadOnlyMemory<byte> BuildPayload(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }

    private static OutboundMessage[] BuildBatch(ReadOnlyMemory<byte> payload, int count)
    {
        var messages = new OutboundMessage[count];
        for (int i = 0; i < count; i++)
        {
            messages[i] = new OutboundMessage(
                routingKey: string.Empty,
                headers: EmptyHeaders,
                body: payload,
                contentType: "application/octet-stream");
        }

        return messages;
    }

    // A completion source that carries the confirmation count it waits for, so the pair is published and
    // read atomically as one reference.
    private sealed class TargetWaiter(long target)
        : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        internal long Target { get; } = target;
    }
}
