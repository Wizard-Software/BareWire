using BareWire.Abstractions;
using BareWire.Abstractions.Transport;

namespace BareWire.IntegrationTests.Transport.Parity;

/// <summary>
/// Holds one <see cref="IAsyncEnumerator{T}"/> open against a single queue for the lifetime of a parity
/// scenario. Settlement on a live broker requires a live consumer channel, so every scenario that reads
/// a queue keeps this reader open (never a <c>break</c> out of an <c>await foreach</c>) until the
/// scenario is done with that queue.
/// </summary>
/// <remarks>
/// Owns a <see cref="CancellationTokenSource"/> linked to the caller's token so <see cref="DisposeAsync"/>
/// can cancel a still-pending <c>MoveNextAsync</c> before disposing the enumerator, rather than letting
/// the enumerator's disposal race a delivery that never arrives. Declare this after the adapter in an
/// <c>await using</c> chain so it is disposed first.
/// </remarks>
internal sealed class ParityQueueReader : IAsyncDisposable
{
    private readonly IAsyncEnumerator<InboundMessage> _enumerator;
    private readonly CancellationTokenSource _linkedCts;

    private ParityQueueReader(IAsyncEnumerator<InboundMessage> enumerator, CancellationTokenSource linkedCts)
    {
        _enumerator = enumerator;
        _linkedCts = linkedCts;
    }

    /// <summary>
    /// Opens a reader against <paramref name="queue"/>. The returned reader does not become an "active
    /// consumer" on the transport until <see cref="NextAsync"/> is first called — <c>ConsumeAsync</c> is
    /// lazy on both transports.
    /// </summary>
    /// <param name="adapter">The adapter to consume from.</param>
    /// <param name="queue">The queue name to consume from.</param>
    /// <param name="cancellationToken">The scenario's cancellation token; the reader links its own token to it.</param>
    internal static ParityQueueReader Open(ITransportAdapter adapter, string queue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FlowControlOptions flowControl = new();
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(queue, flowControl, linkedCts.Token).GetAsyncEnumerator(linkedCts.Token);
        return new ParityQueueReader(enumerator, linkedCts);
    }

    /// <summary>
    /// Advances the stream and returns the next delivered message.
    /// </summary>
    /// <exception cref="InvalidOperationException">The consume stream ended before a message arrived.</exception>
    internal async Task<InboundMessage> NextAsync()
    {
        if (!await _enumerator.MoveNextAsync())
        {
            throw new InvalidOperationException("The consume stream ended before a message was received.");
        }

        return _enumerator.Current;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _linkedCts.CancelAsync();

        try
        {
            await _enumerator.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected: cancelling the linked token while a MoveNextAsync is in flight completes the
            // enumerator's disposal via cancellation instead of a clean stop.
        }
        finally
        {
            _linkedCts.Dispose();
        }
    }
}
