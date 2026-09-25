using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;

namespace BareWire.Testing;

/// <summary>
/// Decorates the <see cref="ITransportAdapter"/> registered by <c>AddBareWireWithInMemory</c> with an
/// outbound-message observation hook, while transparently delegating every optional coordination seam
/// the wrapped transport implements.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BareWireTestHarness"/> registers this decorator in place of the plain transport adapter
/// so it can observe outbound sends without requiring <c>InternalsVisibleTo</c> from the in-memory
/// transport package — the in-memory transport does not expose a public "message sent" hook.
/// </para>
/// <para>
/// This type mirrors exactly the set of optional seams the in-memory transport implements —
/// <see cref="IGracefulDrainTransport"/>, <see cref="ITransportHealthSource"/>,
/// <see cref="INativeMessageScheduler"/>, <see cref="IAsyncDisposable"/>, and <see cref="IDisposable"/>.
/// It deliberately does not implement the consumer-channel-manager or durable-park-settlement seams the
/// in-memory transport does not implement either — adding either here would change what a runtime cast
/// to that seam resolves to for the bus core, breaking the coordination protocol between the core and
/// the transport it wraps.
/// </para>
/// </remarks>
internal sealed class ObservingTransportAdapter
    : ITransportAdapter, IGracefulDrainTransport, ITransportHealthSource, INativeMessageScheduler,
      IAsyncDisposable, IDisposable
{
    private int _drainCallCount;

    /// <summary>
    /// Initializes a new instance wrapping <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The transport adapter to delegate every operation to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    internal ObservingTransportAdapter(ITransportAdapter inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Inner = inner;
    }

    /// <summary>Gets the wrapped transport adapter.</summary>
    internal ITransportAdapter Inner { get; }

    /// <summary>
    /// Gets the number of times <see cref="DrainAsync"/> completed a delegated call to the wrapped
    /// transport's own <see cref="IGracefulDrainTransport.DrainAsync"/> without it throwing.
    /// </summary>
    internal int DrainCallCount => Volatile.Read(ref _drainCallCount);

    /// <summary>
    /// Raised after every <see cref="OutboundMessage"/> in a batch has been handed to the wrapped
    /// transport's <see cref="SendBatchAsync"/>, in input order, regardless of the resulting
    /// <see cref="SendResult"/>. Used by <see cref="BareWireTestHarness"/> to observe outbound
    /// messages without polling.
    /// </summary>
    internal event Action<OutboundMessage>? MessageSent;

    /// <inheritdoc />
    public string TransportName => Inner.TransportName;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => Inner.Capabilities;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SendResult> results =
            await Inner.SendBatchAsync(messages, cancellationToken).ConfigureAwait(false);

        foreach (OutboundMessage message in messages)
            MessageSent?.Invoke(message);

        return results;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        CancellationToken cancellationToken = default)
        => Inner.ConsumeAsync(endpointName, flowControl, cancellationToken);

    /// <inheritdoc />
    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
        => Inner.SettleAsync(action, message, cancellationToken);

    /// <inheritdoc />
    public Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
        => Inner.DeployTopologyAsync(topology, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="DrainCallCount"/> is incremented only after the delegated call completes
    /// successfully — a failing delegation (swallowed and logged by the bus during shutdown) must
    /// not be counted as a completed drain.
    /// </remarks>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await RequireSeam<IGracefulDrainTransport>().DrainAsync(timeout, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _drainCallCount);
    }

    /// <inheritdoc />
    public BusHealthStatus GetHealth() => RequireSeam<ITransportHealthSource>().GetHealth();

    /// <inheritdoc />
    public Task<ScheduledMessageToken> ScheduleAsync(
        OutboundMessage message,
        DateTimeOffset scheduledEnqueueTime,
        CancellationToken cancellationToken = default)
        => RequireSeam<INativeMessageScheduler>().ScheduleAsync(message, scheduledEnqueueTime, cancellationToken);

    /// <inheritdoc />
    public Task CancelScheduledAsync(ScheduledMessageToken token, CancellationToken cancellationToken = default)
        => RequireSeam<INativeMessageScheduler>().CancelScheduledAsync(token, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        switch (Inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Inner is IDisposable disposable)
            disposable.Dispose();
    }

    private TSeam RequireSeam<TSeam>() where TSeam : class
        => Inner as TSeam ?? throw new InvalidOperationException(
            $"The wrapped transport adapter '{Inner.GetType().Name}' does not implement '{typeof(TSeam).Name}'.");
}
