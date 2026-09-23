using System.Buffers;
using System.Runtime.CompilerServices;
using BareWire.Abstractions.Transport;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Runs one <see cref="InMemoryTransportAdapter.ConsumeAsync"/> call: reads deliveries from a single
/// <see cref="InMemoryQueue"/>, registers every delivery it hands out in the adapter's
/// <see cref="InMemoryDeliveryMap"/>, and, when its enumerator ends for any reason, puts the deliveries it
/// handed out and that were never settled back at the head of the queue — or, when the transport is
/// shutting down, drops them and releases their queue slots. This mirrors a RabbitMQ channel closing with
/// unacknowledged messages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buffer ownership.</b> The buffer of a handed-out delivery belongs to the <see cref="InboundMessage"/>
/// it was handed out in (its <c>Dispose</c> returns it to the pool); this runner and the delivery map never
/// return it. A requeued delivery therefore carries a copy of the body in a newly rented buffer, taken
/// before the map entry is claimed: if the claim wins, the original buffer was valid for the whole copy; if
/// it loses (the delivery was settled concurrently), the copy is returned to the pool. When the consume
/// loop never disposes a message (for example a consumer cancelled while waiting for credit), the original
/// buffer is left to the garbage collector — a missed return, never a double return.
/// </para>
/// <para>
/// <b>At-least-once.</b> The consume loop may dispose this enumerator while messages it handed out are
/// still being processed (for example by per-key ordering lanes during a stop, or across a single-active
/// consumer handover). Those messages are requeued and may be processed twice; a late settlement of the
/// original finds no map entry and must be a no-op. A message the consume loop disposed without settling it
/// (its buffer already back in the pool) is detected and dropped with a warning instead of being copied. In
/// the narrow window where such a message is disposed while the copy is being taken, the copy may still read
/// a buffer already returned to the pool — a known limit the adapter cannot observe.
/// </para>
/// </remarks>
internal sealed class InMemoryQueueRunner
{
    private readonly InMemoryDeliveryMap _map;
    private readonly SemaphoreSlim? _singleActiveGate;
    private readonly CancellationToken _shutdownToken;
    private readonly InMemoryConsumeDiagnostics _diagnostics;

    /// <param name="queue">The queue to consume.</param>
    /// <param name="map">The adapter's delivery map.</param>
    /// <param name="singleActiveGate">
    /// The queue's single-active-consumer gate, or <see langword="null"/> when the queue has none. At most
    /// one runner holds the gate; the others wait on it in standby without counting as active consumers.
    /// </param>
    /// <param name="diagnostics">Reports deliveries dropped on shutdown.</param>
    /// <param name="shutdownToken">Cancelled when the adapter shuts down.</param>
    internal InMemoryQueueRunner(
        InMemoryQueue queue,
        InMemoryDeliveryMap map,
        SemaphoreSlim? singleActiveGate,
        InMemoryConsumeDiagnostics diagnostics,
        CancellationToken shutdownToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Queue = queue;
        _map = map;
        _singleActiveGate = singleActiveGate;
        _shutdownToken = shutdownToken;
        _diagnostics = diagnostics;
    }

    /// <summary>Gets the queue this runner consumes.</summary>
    internal InMemoryQueue Queue { get; }

    /// <summary>
    /// Yields one <see cref="InboundMessage"/> per delivery read from <see cref="Queue"/>. Ends with an
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> or the adapter's
    /// shutdown token is cancelled; ends immediately when the adapter has already shut down.
    /// </summary>
    internal async IAsyncEnumerable<InboundMessage> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_shutdownToken.IsCancellationRequested)
        {
            yield break;
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        bool gateAcquired = false;
        try
        {
            if (_singleActiveGate is not null)
            {
                await _singleActiveGate.WaitAsync(linked.Token).ConfigureAwait(false);
                gateAcquired = true;
            }

            await foreach (InMemoryDelivery delivery in Queue.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                ulong deliveryTag = _map.NextTag();
                var message = new InboundMessage(
                    delivery.MessageId,
                    delivery.InboundHeaders,
                    new ReadOnlySequence<byte>(delivery.Body),
                    deliveryTag,
                    pooledBuffer: delivery.Buffer);
                _map.Add(deliveryTag, new InFlightDelivery(delivery, Queue, this, message));
                yield return message;
            }
        }
        finally
        {
            try
            {
                ReleaseUnsettled();
            }
            finally
            {
                if (gateAcquired)
                {
                    _singleActiveGate!.Release();
                }

                linked.Dispose();
            }
        }
    }

    /// <summary>
    /// Claims every delivery this runner handed out and nobody settled, then either drops them (shutdown:
    /// release the queue slot, never the buffer) or requeues a copy of each at the head of the queue.
    /// </summary>
    private void ReleaseUnsettled()
    {
        List<KeyValuePair<ulong, InFlightDelivery>> owned = _map.SnapshotOwnedBy(this);
        if (owned.Count == 0)
        {
            return;
        }

        if (_shutdownToken.IsCancellationRequested)
        {
            int dropped = 0;
            foreach (KeyValuePair<ulong, InFlightDelivery> pair in owned)
            {
                if (_map.TryTake(pair.Key, out InFlightDelivery entry))
                {
                    entry.Queue.ReleaseSlot();
                    dropped++;
                }
            }

            _diagnostics.DeliveriesDroppedOnShutdown(Queue.Name, dropped);
            return;
        }

        var requeue = new List<InMemoryDelivery>(owned.Count);
        int disposedUnsettled = 0;
        foreach (KeyValuePair<ulong, InFlightDelivery> pair in owned)
        {
            // Taking the entry first makes a concurrent settlement a no-op; losing the race means the delivery
            // was settled and its message owns the buffer as usual.
            if (!_map.TryTake(pair.Key, out InFlightDelivery entry))
            {
                continue;
            }

            // Consumers may still be processing (and disposing) messages on other threads, e.g. ordered lanes
            // that drain after this enumerator ends. Pinning is atomic with the message's Dispose: if the
            // consumer disposed first, the buffer is already back in the pool and the body can no longer be
            // read, so the delivery is dropped and its slot freed. A Dispose that lands while the body is
            // pinned leaves the buffer to the unpin, so it returns to the pool exactly once, after the copy.
            if (!entry.Message.TryPinPooledBuffer())
            {
                Queue.ReleaseSlot();
                disposedUnsettled++;
                continue;
            }

            InMemoryDelivery delivery = entry.Delivery;
            byte[] copy = ArrayPool<byte>.Shared.Rent(Math.Max(delivery.Length, 1));
            try
            {
                delivery.Body.Span.CopyTo(copy);
            }
            finally
            {
                entry.Message.UnpinPooledBuffer();
            }

            requeue.Add(delivery.CreateRedelivery(copy, delivery.Length));
        }

        _diagnostics.DeliveriesDisposedUnsettled(Queue.Name, disposedUnsettled);

        // The adapter may have started shutting down while the copies were taken: drop instead of requeueing,
        // so nothing is put back on a queue of a disposed adapter.
        if (_shutdownToken.IsCancellationRequested)
        {
            foreach (InMemoryDelivery copied in requeue)
            {
                Queue.ReleaseSlot();
                ArrayPool<byte>.Shared.Return(copied.Buffer);
            }

            _diagnostics.DeliveriesDroppedOnShutdown(Queue.Name, requeue.Count);
            return;
        }

        Queue.RequeueAtHead(requeue);
    }
}
