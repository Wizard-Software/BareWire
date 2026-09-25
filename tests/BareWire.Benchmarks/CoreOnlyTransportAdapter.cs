using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;

namespace BareWire.Benchmarks;

/// <summary>
/// A local, in-process <see cref="ITransportAdapter"/> fake that measures the BareWire core pipeline
/// in isolation, without the cost of any real transport engine (routing, buffer pooling, queue
/// bookkeeping). <see cref="SendBatchAsync"/> is a pure sink — every message is confirmed without being
/// stored or copied. <see cref="ConsumeAsync"/> drains a bounded, in-process channel that the benchmark
/// fills ahead of time via <see cref="TryEnqueue"/>.
/// </summary>
/// <remarks>
/// Not registered anywhere outside this project and never packaged — see the <c>IsPackable</c> setting
/// in <c>BareWire.Benchmarks.csproj</c>.
/// </remarks>
internal sealed class CoreOnlyTransportAdapter : ITransportAdapter
{
    private const int MaxCachedBatchSize = 64;
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    private readonly Channel<InboundMessage> _consumeChannel;

    // Cached, already-confirmed results for the batch sizes the bus's outbound publishing loop
    // actually sends (1..64) — built lazily so an adapter that only ever sees one batch size never
    // allocates the others. Sized 0..MaxCachedBatchSize so the batch size itself indexes it directly.
    private readonly Task<IReadOnlyList<SendResult>>?[] _cachedSendResults = new Task<IReadOnlyList<SendResult>>?[MaxCachedBatchSize + 1];

    private readonly TaskCompletionSource _allSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _sentCount;
    private int _settledCount;
    private int _settleTarget = int.MaxValue;

    /// <summary>
    /// Initializes a new instance of <see cref="CoreOnlyTransportAdapter"/> with a bounded consume
    /// channel of the given capacity.
    /// </summary>
    /// <param name="consumeCapacity">
    /// The capacity of the internal consume channel — must be at least the number of messages a
    /// benchmark iteration intends to enqueue via <see cref="TryEnqueue"/>. Defaults to <c>4,096</c>.
    /// </param>
    internal CoreOnlyTransportAdapter(int consumeCapacity = 4_096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(consumeCapacity, 0);

        _consumeChannel = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(consumeCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <inheritdoc />
    public string TransportName => "CoreOnlyFake";

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.None;

    /// <summary>Gets the total number of messages accepted by <see cref="SendBatchAsync"/> so far.</summary>
    internal long SentCount => Volatile.Read(ref _sentCount);

    /// <summary>
    /// Confirms every message in <paramref name="messages"/> without storing or copying its body.
    /// Batch sizes from 1 to 64 (the bus's outbound publishing loop never sends a larger batch) reuse an
    /// instance-cached, already-completed result array — zero transport-side allocation per call.
    /// </summary>
    /// <inheritdoc />
    public Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        int count = messages.Count;
        Interlocked.Add(ref _sentCount, count);

        if (count == 0)
        {
            return Task.FromResult<IReadOnlyList<SendResult>>([]);
        }

        return count <= MaxCachedBatchSize
            ? Volatile.Read(ref _cachedSendResults[count]) ?? CacheAndGet(count)
            : Task.FromResult<IReadOnlyList<SendResult>>(BuildConfirmedResults(count));
    }

    /// <summary>
    /// Yields messages previously handed to <see cref="TryEnqueue"/> from the bounded consume channel.
    /// The endpoint name is ignored — this fake serves exactly one logical queue. The stream blocks once
    /// the channel is empty and continues only until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <inheritdoc />
    public IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);

        return _consumeChannel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>
    /// Records the settlement with an <see cref="Interlocked"/> counter — no broker round-trip, no
    /// buffer return (Core-only messages are built without a pooled buffer; see
    /// <c>ConsumeBenchmarks</c>). Signals any pending <see cref="WaitForSettledAsync"/> call once the
    /// counter reaches its target.
    /// </summary>
    /// <inheritdoc />
    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        int settled = Interlocked.Increment(ref _settledCount);
        if (settled >= Volatile.Read(ref _settleTarget))
        {
            _allSettled.TrySetResult();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Enqueues a pre-built message onto the bounded consume channel for a later
    /// <see cref="ConsumeAsync"/> call to yield.
    /// </summary>
    /// <param name="message">The message to enqueue. Must not be null.</param>
    /// <returns>
    /// <see langword="true"/> when the message was accepted; <see langword="false"/> when the channel
    /// is at the capacity passed to the constructor.
    /// </returns>
    internal bool TryEnqueue(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _consumeChannel.Writer.TryWrite(message);
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> messages have been settled via
    /// <see cref="SettleAsync"/>, up to a fixed 30-second deadline.
    /// </summary>
    /// <param name="count">The cumulative number of settled messages to wait for.</param>
    /// <param name="cancellationToken">A token that, when cancelled, also stops the wait.</param>
    /// <exception cref="TimeoutException">
    /// Thrown when <paramref name="count"/> settlements are not observed within 30 seconds — the
    /// benchmark then fails loudly (<c>NA</c>) instead of reporting a silently truncated measurement.
    /// </exception>
    internal async Task WaitForSettledAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        // Interlocked.Exchange is a full fence: a plain volatile write followed by a volatile read may be
        // reordered on weak memory models (Arm64), letting this thread read a stale settled count while a
        // concurrent SettleAsync reads the stale target — both would miss the completion and the wait
        // would hang. SettleAsync pairs this with its own full fence (Interlocked.Increment).
        if (Interlocked.Exchange(ref _settleTarget, count) != int.MaxValue)
        {
            throw new InvalidOperationException(
                "WaitForSettledAsync supports a single wait per adapter instance; create a new adapter.");
        }

        if (Volatile.Read(ref _settledCount) >= count)
        {
            _allSettled.TrySetResult();
        }

        using CancellationTokenSource timeoutCts = new(SettleTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await _allSettled.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"CoreOnlyTransportAdapter did not observe {count} settled message(s) within " +
                $"{SettleTimeout} (observed {Volatile.Read(ref _settledCount)}).");
        }
    }

    private Task<IReadOnlyList<SendResult>> CacheAndGet(int count)
    {
        Task<IReadOnlyList<SendResult>> built =
            Task.FromResult<IReadOnlyList<SendResult>>(BuildConfirmedResults(count));
        Interlocked.CompareExchange(ref _cachedSendResults[count], built, null);
        return _cachedSendResults[count]!;
    }

    private static SendResult[] BuildConfirmedResults(int count)
    {
        var results = new SendResult[count];
        Array.Fill(results, new SendResult(IsConfirmed: true, DeliveryTag: 0));
        return results;
    }
}
