using System.Collections.Frozen;
using System.Collections.Immutable;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Implements the settlement half of the in-memory transport's RabbitMQ-mirroring delivery contract:
/// <see cref="SettlementAction.Ack"/> only releases the source queue's slot; <see cref="SettlementAction.Nack"/>
/// and <see cref="SettlementAction.Reject"/> dead-letter; <see cref="SettlementAction.Requeue"/> puts a
/// copy back at the head of the source queue unless <c>InMemoryTransportOptions.MaxRedeliveries</c> is
/// reached, in which case it dead-letters too. <see cref="SettlementAction.Defer"/> — reachable only when
/// <c>InMemoryTransportOptions.DeferEnabled</c> is on, since the caller
/// (<see cref="InMemoryTransportAdapter.SettleAsync"/>) throws <see cref="NotSupportedException"/> for it
/// otherwise, before an entry is ever claimed — copies the body and hands the copy to the configured
/// <see cref="InMemoryDeferScheduler"/> for delayed redelivery to the same, already-reserved slot.
/// </summary>
/// <remarks>
/// <para>
/// <b>One buffer owner per copy, always.</b> Every path that dead-letters or requeues rents its copy from
/// <see cref="InMemoryBufferPool"/> and hands it to exactly one <see cref="InMemoryQueue.WriteReserved"/>
/// call, or returns it to the pool if none succeeds — never both, never neither.
/// </para>
/// <para>
/// <b>Dead-letter fan-out ordering.</b> When a dead-letter exchange fans out to more than one queue, every
/// additional copy is rented and filled from the still-unpublished first copy BEFORE any of the reserved
/// target queues is written to. Only once every copy exists does <see cref="DeadLetter"/> start writing —
/// the extra copies first, the first (source) copy LAST. This is the one ordering that cannot use-after-return:
/// writing the first copy earlier would let a consumer of that queue dispose it — returning the buffer to
/// the pool — while it is still being read as the source of a copy for a later queue.
/// </para>
/// </remarks>
internal sealed class InMemorySettlement
{
    private readonly InMemoryTransportOptions _options;
    private readonly InMemoryBroker _broker;
    private readonly InMemoryRouter _router;
    private readonly InMemoryBufferPool _pool;
    private readonly InMemoryDeferScheduler? _deferScheduler;
    private readonly InMemoryConsumeDiagnostics _diagnostics;
    private readonly FrozenDictionary<string, DeadLetterTarget> _deadLetterTargets;

    /// <param name="options">The transport options settlement reads <c>MaxRedeliveries</c> and <c>DeferDelay</c> from.</param>
    /// <param name="registry">The sealed topology registry the dead-letter target table is built from.</param>
    /// <param name="broker">Resolves dead-letter target queue instances by name.</param>
    /// <param name="router">Resolves the dead-letter exchange's target queues.</param>
    /// <param name="pool">The pool every settlement copy is rented from and returned to.</param>
    /// <param name="deferScheduler">
    /// The scheduler <see cref="SettlementAction.Defer"/> hands its copy to, or <see langword="null"/> when
    /// <c>InMemoryTransportOptions.DeferEnabled</c> is off — in which case <see cref="Settle"/> never
    /// receives <see cref="SettlementAction.Defer"/> at all (the caller rejects it earlier).
    /// </param>
    /// <param name="diagnostics">Records dropped deliveries.</param>
    internal InMemorySettlement(
        InMemoryTransportOptions options,
        ExchangeRegistry registry,
        InMemoryBroker broker,
        InMemoryRouter router,
        InMemoryBufferPool pool,
        InMemoryDeferScheduler? deferScheduler,
        InMemoryConsumeDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _options = options;
        _broker = broker;
        _router = router;
        _pool = pool;
        _deferScheduler = deferScheduler;
        _diagnostics = diagnostics;
        _deadLetterTargets = BuildDeadLetterTargets(registry);
    }

    /// <summary>
    /// Settles a claimed entry synchronously. Never waits for capacity, never touches the caller's
    /// cancellation token (there is nothing here that ever waits).
    /// </summary>
    /// <param name="action">
    /// The settlement action. Must be <see cref="SettlementAction.Ack"/>, <see cref="SettlementAction.Nack"/>,
    /// <see cref="SettlementAction.Reject"/>, <see cref="SettlementAction.Requeue"/>, or
    /// <see cref="SettlementAction.Defer"/> (only when a <see cref="InMemoryDeferScheduler"/> was supplied
    /// at construction) — the caller has already validated the action.
    /// </param>
    /// <param name="entry">The claimed in-flight entry.</param>
    internal void Settle(SettlementAction action, InFlightDelivery entry)
    {
        switch (action)
        {
            case SettlementAction.Ack:
                // The message owns its buffer; its own Dispose() returns it. This never touches it.
                entry.Queue.ReleaseSlot();
                return;

            case SettlementAction.Requeue:
                Requeue(entry);
                return;

            case SettlementAction.Nack:
            case SettlementAction.Reject:
                DeadLetter(entry, SettlementDropReason.NoDeadLetterExchange);
                return;

            case SettlementAction.Defer:
                Defer(entry);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown settlement action.");
        }
    }

    /// <summary>
    /// Copies <paramref name="entry"/>'s body and hands the copy, as the next redelivery, to
    /// <see cref="_deferScheduler"/> for delayed write-back to the same, already-reserved slot.
    /// </summary>
    /// <remarks>
    /// Only called when <see cref="_deferScheduler"/> is non-<see langword="null"/> — <see cref="Settle"/>
    /// only receives <see cref="SettlementAction.Defer"/> when the caller's constructor supplied one (which
    /// happens exactly when <c>InMemoryTransportOptions.DeferEnabled</c> is on).
    /// </remarks>
    private void Defer(InFlightDelivery entry)
    {
        if (!InMemoryBodyCopier.TryCopy(entry, _pool, out byte[] copy))
        {
            entry.Queue.ReleaseSlot();
            _diagnostics.DeliveriesDisposedUnsettled(entry.Queue.Name, 1);
            return;
        }

        InMemoryDelivery delivery = entry.Delivery;
        InMemoryDelivery redelivery = delivery.CreateRedelivery(copy, delivery.Length);

        // The redelivery already holds the source queue's reserved slot; TrySchedule takes ownership of
        // both the slot and the copy on true — see InMemoryDeferScheduler's remarks.
        if (_deferScheduler!.TrySchedule(entry.Queue, redelivery, _options.DeferDelay))
        {
            return;
        }

        // The scheduler was already disposed before it could take ownership: this call never claimed
        // anything, so the copy and the slot are still ours to clean up — same accounting as every other
        // shutdown-triggered drop.
        entry.Queue.ReleaseSlot();
        _pool.Return(copy);
        _diagnostics.DeliveriesDroppedOnShutdown(entry.Queue.Name, 1);
    }

    /// <summary>
    /// Builds the dead-letter target table: one entry per queue in <paramref name="registry"/> that
    /// declares an <c>x-dead-letter-exchange</c> argument whose value is a <see cref="string"/> — any
    /// other argument shape (missing, non-string, or a queue with no arguments at all) means the queue has
    /// no dead-letter target.
    /// </summary>
    internal static FrozenDictionary<string, DeadLetterTarget> BuildDeadLetterTargets(ExchangeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var targets = new Dictionary<string, DeadLetterTarget>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, QueueDeclaration> entry in registry.Queues)
        {
            IReadOnlyDictionary<string, object>? arguments = entry.Value.Arguments;
            if (arguments is null)
            {
                continue;
            }

            if (!arguments.TryGetValue("x-dead-letter-exchange", out object? exchangeValue)
                || exchangeValue is not string exchange)
            {
                continue;
            }

            string? routingKey = arguments.TryGetValue("x-dead-letter-routing-key", out object? routingKeyValue)
                && routingKeyValue is string routingKeyString
                    ? routingKeyString
                    : null;

            targets[entry.Key] = new DeadLetterTarget(exchange, routingKey);
        }

        return targets.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Requeues <paramref name="entry"/> at the head of its own queue with an incremented redelivery
    /// count, or dead-letters it when <c>MaxRedeliveries</c> is already reached.
    /// </summary>
    private void Requeue(InFlightDelivery entry)
    {
        InMemoryDelivery delivery = entry.Delivery;
        if (delivery.RedeliveryCount >= _options.MaxRedeliveries)
        {
            DeadLetter(entry, SettlementDropReason.MaxRedeliveries);
            return;
        }

        if (!InMemoryBodyCopier.TryCopy(entry, _pool, out byte[] copy))
        {
            entry.Queue.ReleaseSlot();
            _diagnostics.DeliveriesDisposedUnsettled(entry.Queue.Name, 1);
            return;
        }

        // The copy already holds the source queue's reserved slot (Requeue never releases it). An
        // exception here means the copy may already have been written — see the single-delivery
        // RequeueAtHead overload's remarks — so it is never returned to the pool on that path; it simply
        // propagates.
        entry.Queue.RequeueAtHead(delivery.CreateRedelivery(copy, delivery.Length));
    }

    /// <summary>
    /// Dead-letters <paramref name="entry"/>: resolves its source queue's DLX target, copies the body, and
    /// fans the copy out to every queue the DLX routes to — or drops it, with
    /// <paramref name="noTargetReason"/> or the applicable reason, when no target accepts it.
    /// </summary>
    /// <param name="entry">The claimed in-flight entry being dead-lettered.</param>
    /// <param name="noTargetReason">
    /// The drop reason reported when the source queue declares no dead-letter exchange —
    /// <see cref="SettlementDropReason.NoDeadLetterExchange"/> for <c>Nack</c>/<c>Reject</c>,
    /// <see cref="SettlementDropReason.MaxRedeliveries"/> for a <c>Requeue</c> past the redelivery limit.
    /// </param>
    private void DeadLetter(InFlightDelivery entry, SettlementDropReason noTargetReason)
    {
        InMemoryQueue sourceQueue = entry.Queue;
        string sourceQueueName = sourceQueue.Name;

        // M1 (mandatory mitigation): resolve the DLX target and route BEFORE any copy is rented, so a
        // drop that needs no copy (no target declared, or the target is unroutable) never rents one.
        if (!_deadLetterTargets.TryGetValue(sourceQueueName, out DeadLetterTarget target))
        {
            sourceQueue.ReleaseSlot();
            _diagnostics.SettlementDropped(sourceQueueName, noTargetReason);
            return;
        }

        string routingKey = target.RoutingKey ?? ResolveOriginalRoutingKey(entry.Delivery);
        InMemoryRouteResult route = _router.Route(target.Exchange, routingKey);
        if (!route.IsRouted)
        {
            sourceQueue.ReleaseSlot();
            _diagnostics.SettlementDropped(sourceQueueName, SettlementDropReason.Unroutable);
            return;
        }

        if (!InMemoryBodyCopier.TryCopy(entry, _pool, out byte[] firstCopy))
        {
            sourceQueue.ReleaseSlot();
            _diagnostics.DeliveriesDisposedUnsettled(sourceQueueName, 1);
            return;
        }

        int length = entry.Delivery.Length;
        InMemoryHeaderSet headers = entry.Delivery.Headers;
        sourceQueue.ReleaseSlot();

        PublishDeadLetterCopies(sourceQueueName, route.Queues, firstCopy, length, headers);
    }

    /// <summary>
    /// Reserves a slot on every queue <paramref name="targetQueueNames"/> names that has room, fills one
    /// copy per reserved queue (<paramref name="firstCopy"/> reused for the first, freshly rented copies
    /// for the rest), and writes every extra copy before writing <paramref name="firstCopy"/> — see the
    /// ordering rule in this type's remarks. Every buffer not written to a queue by the time this method
    /// returns — including on an exception from <see cref="InMemoryQueue.WriteReserved"/> — is returned to
    /// the pool, and every slot reserved but not written to is released.
    /// </summary>
    private void PublishDeadLetterCopies(
        string sourceQueueName,
        ImmutableArray<string> targetQueueNames,
        byte[] firstCopy,
        int length,
        InMemoryHeaderSet headers)
    {
        var reserved = new List<InMemoryQueue>(targetQueueNames.Length);
        foreach (string targetName in targetQueueNames)
        {
            if (_broker.TryGetQueue(targetName, out InMemoryQueue? queue)
                && queue.TryReserve() == QueueReservationResult.Reserved)
            {
                reserved.Add(queue);
            }
        }

        if (reserved.Count == 0)
        {
            _pool.Return(firstCopy);
            _diagnostics.SettlementDropped(sourceQueueName, SettlementDropReason.DeadLetterQueueFull);
            return;
        }

        // Every extra copy is rented and filled from the still-unpublished firstCopy BEFORE any
        // WriteReserved runs below — see this type's remarks on dead-letter fan-out ordering (M1).
        var buffers = new byte[reserved.Count][];
        buffers[0] = firstCopy;
        for (int i = 1; i < reserved.Count; i++)
        {
            byte[] extra = _pool.Rent(length);
            firstCopy.AsSpan(0, length).CopyTo(extra);
            buffers[i] = extra;
        }

        var written = new bool[reserved.Count];
        try
        {
            // Extra copies first, the first (source) copy LAST — the ordering rule this type's remarks
            // document: writing the first copy earlier would let a fast consumer of that queue dispose it,
            // returning the buffer to the pool, while it is still being read as the source of a later copy.
            for (int i = reserved.Count - 1; i >= 1; i--)
            {
                reserved[i].WriteReserved(new InMemoryDelivery(buffers[i], length, headers, redeliveryCount: 0));
                written[i] = true;
            }

            reserved[0].WriteReserved(new InMemoryDelivery(buffers[0], length, headers, redeliveryCount: 0));
            written[0] = true;
        }
        finally
        {
            // Mirrors InMemorySender.Commit's try/finally: on any exception (an invariant violation, since
            // every entry here holds a matching reservation), every buffer not yet handed to a queue is
            // returned and every slot not yet written to is released, then the exception propagates.
            for (int i = 0; i < reserved.Count; i++)
            {
                if (!written[i])
                {
                    _pool.Return(buffers[i]);
                    reserved[i].ReleaseSlot();
                }
            }
        }
    }

    /// <summary>
    /// Resolves the routing key a dead-lettered delivery's original publish used, from its stamped
    /// <see cref="InMemoryHeaderNames.RoutingKey"/> header — the empty string when the header is absent
    /// (an empty header set, for example).
    /// </summary>
    private static string ResolveOriginalRoutingKey(InMemoryDelivery delivery) =>
        delivery.Headers.TryGetValue(InMemoryHeaderNames.RoutingKey, out string? routingKey) ? routingKey : string.Empty;
}
