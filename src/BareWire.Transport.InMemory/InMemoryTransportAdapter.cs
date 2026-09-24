using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory.Internal;
using BareWire.Transport.InMemory.Topology;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.Transport.InMemory;

/// <summary>
/// In-memory transport adapter. At construction it interprets the configured
/// <see cref="InMemoryTransportOptions.Topology"/> (plus receive-endpoint queue names and per-type
/// exchange mappings) into a sealed <see cref="Registry"/> — an invalid configuration throws
/// <see cref="BareWireConfigurationException"/> here, at bus build time, rather than on the wire. An
/// exchange type or queue argument the in-memory transport does not support throws
/// <see cref="BareWireTransportException"/> at the same point, and so does a receive endpoint declaring
/// <see cref="TransportAffinity.ConsistentHash"/> (as a <see cref="BareWireConfigurationException"/>).
/// Once built, the topology is frozen: <see cref="DeployTopologyAsync"/> accepts only the same declaration
/// (idempotent no-op) and rejects any other one, and <see cref="ConsumeAsync"/> refuses to start on an
/// undeclared queue. <see cref="SendBatchAsync"/> accepts a batch of outbound messages per its own
/// remarks. <see cref="SettleAsync"/> fully mirrors RabbitMQ's settlement map — <c>Ack</c>, <c>Nack</c>,
/// <c>Reject</c>, and <c>Requeue</c> (with a redelivery limit falling back to dead-lettering) are always
/// available; <c>Defer</c> throws <see cref="NotSupportedException"/> unless
/// <see cref="InMemoryTransportOptions.DeferEnabled"/> is on, in which case it schedules a delayed
/// redelivery instead. Also implements <see cref="IGracefulDrainTransport"/>, letting the bus wait —
/// bounded by a timeout and a cancellation token — for every queue with an active consumer to empty
/// before that consumer is cancelled during a graceful shutdown. Registered as an
/// <see cref="ITransportAdapter"/> DI singleton by <c>AddBareWireInMemory</c>.
/// </summary>
internal sealed class InMemoryTransportAdapter
    : ITransportAdapter, IGracefulDrainTransport, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The interval <see cref="DrainAsync"/> polls at while waiting for every active queue to drain. A
    /// fixed, non-configurable constant — small enough to keep the drain's tail latency low, large enough
    /// to keep polling cost negligible against the two counters it checks (a queue's occupancy and the
    /// delivery map's disposed-unsettled entries).
    /// </summary>
    internal static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly FrozenDictionary<string, SemaphoreSlim> _singleActiveGates;
    private readonly InMemoryConsumeDiagnostics _diagnostics;
    private readonly InMemorySendDiagnostics _sendDiagnostics;
    private readonly InMemorySender _sender;
    private readonly InMemorySettlement _settlement;
    private readonly InMemoryDeferScheduler? _deferScheduler;
    private readonly TimeProvider _timeProvider;

    // Never disposed: a runner's finally block may still observe it after shutdown. A cancellation token
    // source without a timer holds no unmanaged resource.
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <param name="options">The transport options the adapter was configured with.</param>
    /// <param name="broker">The container-scoped broker owning the queues.</param>
    /// <param name="logger">An optional logger; defaults to a no-op logger.</param>
    /// <param name="meter">An optional meter for the consume- and send-path counters; no instruments when omitted.</param>
    /// <param name="timeProvider">
    /// An optional time source used to throttle settlement-drop logs (and, once implemented, deferred
    /// redelivery). Defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    /// <param name="bufferPoolObserver">
    /// An optional test hook notified of every rent and return this adapter's own buffer pool
    /// (<see cref="BufferPool"/>) performs. <see langword="null"/> in production.
    /// </param>
    /// <remarks>
    /// An explicit constructor, not a primary one: <see cref="Registry"/> and the send-path collaborators
    /// built from it (<see cref="Router"/>, the sender) must be assigned in the constructor BODY, after
    /// <see cref="Registry"/> itself — a primary constructor's field/property initializers cannot
    /// reference another instance member being initialized in the same constructor.
    /// </remarks>
    internal InMemoryTransportAdapter(
        InMemoryTransportOptions options,
        InMemoryBroker broker,
        ILogger<InMemoryTransportAdapter>? logger = null,
        Meter? meter = null,
        TimeProvider? timeProvider = null,
        IInMemoryBufferPoolObserver? bufferPoolObserver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(broker);

        ILogger effectiveLogger = logger ?? (ILogger)NullLogger<InMemoryTransportAdapter>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Built first, so an unsupported affinity fails before the topology registry is built.
        _singleActiveGates = BuildSingleActiveGates(options);
        _diagnostics = new InMemoryConsumeDiagnostics(effectiveLogger, meter, _timeProvider);
        BufferPool = new InMemoryBufferPool(bufferPoolObserver);

        Options = options;
        Broker = broker;
        Registry = AttachRegistry(broker, InMemoryTopologyInterpreter.BuildRegistry(options));
        InFlight = new InMemoryDeliveryMap();

        Router = new InMemoryRouter(Registry, options, new DelegatingLogger<InMemoryRouter>(effectiveLogger), meter);
        _sendDiagnostics = new InMemorySendDiagnostics(effectiveLogger, Registry, meter);
        _sender = new InMemorySender(broker, Router, options, _sendDiagnostics, IsClosed, BufferPool);
        _deferScheduler = options.DeferEnabled
            ? new InMemoryDeferScheduler(_timeProvider, BufferPool, _diagnostics)
            : null;
        _settlement = new InMemorySettlement(options, Registry, broker, Router, BufferPool, _deferScheduler, _diagnostics);
    }

    /// <inheritdoc />
    public string TransportName => "InMemory";

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.None;

    internal InMemoryTransportOptions Options { get; }

    internal InMemoryBroker Broker { get; }

    /// <summary>
    /// Gets the sealed topology registry built from <see cref="Options"/> at construction time. Never
    /// mutated afterwards — see <see cref="InMemoryTopologyInterpreter.BuildRegistry"/>. Attached to
    /// <see cref="Broker"/> immediately after it is built, so the broker's queues exist by the time
    /// construction completes.
    /// </summary>
    internal ExchangeRegistry Registry { get; }

    /// <summary>
    /// Gets the router the send path resolves target queues with, built from <see cref="Registry"/> at
    /// construction time. Exposed for tests; the send path never needs to look it up elsewhere.
    /// </summary>
    internal InMemoryRouter Router { get; }

    /// <summary>
    /// Gets the deliveries handed to consumers and not settled yet. Settlement, the shutdown sweep, and
    /// each runner's cleanup claim entries from it; see <see cref="TryTakeInFlight"/>.
    /// </summary>
    internal InMemoryDeliveryMap InFlight { get; }

    /// <summary>
    /// Gets the single choke point every rent and return this adapter performs on its own behalf —
    /// settlement copies and each consume runner's requeue-on-abandon copies — goes through. One instance
    /// per adapter; a buffer handed to an <see cref="InboundMessage"/> is returned by that message's own
    /// <see cref="InboundMessage.Dispose"/> without going through this pool.
    /// </summary>
    internal InMemoryBufferPool BufferPool { get; }

    /// <summary>Gets the number of unsettled deliveries dropped because the adapter shut down.</summary>
    internal long DroppedOnShutdownCount => _diagnostics.DroppedOnShutdownCount;

    /// <summary>Gets the total number of rejected copies/messages recorded by the send path so far.</summary>
    internal long SendRejectedCount => _sendDiagnostics.RejectedCount;

    /// <summary>
    /// Gets the number of deliveries dropped because the consumer disposed their messages without settling
    /// them, so their bodies could not be requeued.
    /// </summary>
    internal long DisposedUnsettledCount => _diagnostics.DisposedUnsettledCount;

    /// <summary>Gets the number of deliveries dropped during settlement for <paramref name="reason"/>.</summary>
    internal long SettlementDroppedCount(SettlementDropReason reason) => _diagnostics.SettlementDroppedCount(reason);

    /// <summary>
    /// Gets the number of deferred redeliveries currently pending — scheduled by a <c>Defer</c> settlement
    /// and not yet written back or dropped. Always zero when <see cref="InMemoryTransportOptions.DeferEnabled"/>
    /// is off. A test hook.
    /// </summary>
    internal int PendingDeferCount => _deferScheduler?.PendingCount ?? 0;

    /// <summary>
    /// Gets the number of undelivered messages dropped from queues after they were closed by
    /// <see cref="Dispose"/>. A test hook.
    /// </summary>
    internal long DrainDroppedCount => _diagnostics.DrainDroppedCount;

    private static ExchangeRegistry AttachRegistry(InMemoryBroker broker, ExchangeRegistry registry)
    {
        broker.AttachRegistry(registry);
        return registry;
    }

    private static FrozenDictionary<string, SemaphoreSlim> BuildSingleActiveGates(InMemoryTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var gates = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        foreach (Configuration.InMemoryEndpointConfiguration endpoint in options.EndpointConfigurations)
        {
            TransportAffinity affinity = endpoint.Ordering?.TransportAffinity_ ?? TransportAffinity.None;
            if (affinity == TransportAffinity.ConsistentHash)
            {
                throw new BareWireConfigurationException(
                    optionName: "TransportAffinity",
                    optionValue: $"ConsistentHash (receive endpoint '{endpoint.QueueName}')",
                    expectedValue: "None or SingleActiveConsumer — the in-memory transport has no consistent-hash exchange");
            }

            if (affinity == TransportAffinity.SingleActiveConsumer && !gates.ContainsKey(endpoint.QueueName))
            {
                gates.Add(endpoint.QueueName, new SemaphoreSlim(1, 1));
            }
        }

        return gates.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private bool IsClosed() => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Every message in <paramref name="messages"/> is validated and routed independently: a rejection of
    /// one message (an unresolved exchange, an oversized body, an unroutable routing key with
    /// <see cref="InMemoryTransportOptions.GuaranteedRouting"/> enabled, or a full destination queue)
    /// never affects the others, and every element gets exactly one <see cref="SendResult"/>, in the same
    /// order. <see cref="SendResult.IsConfirmed"/> means a copy of the body reached every one of that
    /// message's target queues — an admission signal, not a durability guarantee (in-memory delivery is
    /// explicitly at-most-once; see the project README). A fan-out message accepted by only some of its
    /// target queues is reported as <see langword="false"/> overall; the copies that WERE accepted stay
    /// accepted. The specific rejection reason is never returned to the caller — it is only observable
    /// through this adapter's logs and metrics, aggregated and throttled, never logged per message.
    /// </para>
    /// <para>
    /// The call waits at most once, for at most <see cref="InMemoryTransportOptions.SendTimeout"/>, no
    /// matter how many messages or target queues in the batch are full — every full queue reached before
    /// that one wait is spent is retried within the same shared budget; every one reached afterwards is
    /// rejected immediately, without waiting. This adapter creates no <see cref="CancellationTokenSource"/>
    /// for either path: the wait's timeout is a parameter of the target queue's own wait (backed by that
    /// queue's <see cref="TimeProvider"/>), so the cost of that one wait — a timer, a promise, and a
    /// queued waiter, roughly 900-975 bytes — is paid by the queue, at most once per call, and only when a
    /// target queue is actually saturated; it is a real cost, not a free one, simply not one this method
    /// incurs directly.
    /// </para>
    /// <para>
    /// Cancelling <paramref name="cancellationToken"/> while the call's one wait is in flight never
    /// throws <see cref="OperationCanceledException"/> from this method: the message that was waiting,
    /// its still-pending target queues, and every later message in the batch are reported as not
    /// confirmed, and the call returns normally. A token already cancelled before any message is
    /// processed DOES throw — see below.
    /// </para>
    /// <para>
    /// Once this adapter is disposed, every queue is closed: a call already waiting for room on a target
    /// queue is woken immediately and reports <see langword="false"/> with reason <c>closed</c> instead of
    /// waiting out the rest of <see cref="InMemoryTransportOptions.SendTimeout"/>, and so does this call —
    /// and any later message of a call already in flight — instead of throwing
    /// <see cref="ObjectDisposedException"/>. A copy that races disposal and reaches a queue's channel a
    /// moment too late is dropped by that queue on arrival: its buffer is returned to <see cref="BufferPool"/>
    /// and its slot released, the same way as every other delivery <see cref="Dispose"/> drops.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="messages"/>, or one of its elements, is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was already cancelled before any message was processed.
    /// </exception>
    public Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is null)
            {
                throw new ArgumentNullException(nameof(messages), $"The message at index {i} is null.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        return messages.Count == 0
            ? Task.FromResult<IReadOnlyList<SendResult>>(Array.Empty<SendResult>())
            : _sender.SendAsync(messages, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Arguments are validated eagerly, before the first element is requested. Each call starts its own
    /// runner reading the queue in FIFO order; with several consumers on one queue, or concurrent
    /// processing in the consume loop, processing order is not guaranteed (as with RabbitMQ and a
    /// prefetch greater than one). Every delivery handed out is tracked by its
    /// <see cref="InboundMessage.DeliveryTag"/> (unique within this adapter) until it is settled. The
    /// message owns its pooled buffer.
    /// </para>
    /// <para>
    /// When the enumeration ends — normally, by cancellation, or because the consume loop stops iterating
    /// — the deliveries this call handed out and nobody settled go back to the head of the queue as
    /// redeliveries (a copy of the body, so the original message stays valid); when the adapter is being
    /// disposed they are dropped instead, releasing their queue slots, with a warning log and a counter.
    /// A message still being processed at that moment may therefore be delivered again (at-least-once);
    /// a late settlement of the original is a no-op.
    /// </para>
    /// <para>
    /// A receive endpoint declaring <see cref="TransportAffinity.SingleActiveConsumer"/> gets at most one
    /// active runner for its queue in this process; further calls wait in standby and take over, starting
    /// with the requeued deliveries, when the active one ends. Only the endpoint's declared affinity enables
    /// this — a queue argument alone does not.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="flowControl"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The adapter has been disposed.</exception>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <paramref name="endpointName"/> names a queue that is neither declared nor
    /// auto-declared in <see cref="Registry"/> — the in-memory transport never creates a queue from
    /// inbound traffic.
    /// </exception>
    public IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!Registry.ContainsQueue(endpointName) || !Broker.TryGetQueue(endpointName, out InMemoryQueue? queue))
        {
            throw new BareWireConfigurationException(
                optionName: "ReceiveEndpoint",
                optionValue: endpointName,
                expectedValue: "a queue declared via ConfigureTopology, or AutoDeclareEndpointQueues() enabled");
        }

        _singleActiveGates.TryGetValue(endpointName, out SemaphoreSlim? gate);
        var runner = new InMemoryQueueRunner(queue, InFlight, gate, _diagnostics, BufferPool, _shutdown.Token);
        return runner.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Claims the in-flight entry of <paramref name="message"/>, removing it from <see cref="InFlight"/>.
    /// The minimal lookup settlement builds on.
    /// </summary>
    /// <remarks>
    /// <see cref="SettleAsync"/> validates its arguments and its settlement action — a null message, an
    /// unknown <see cref="SettlementAction"/> value, or <c>Defer</c> without
    /// <see cref="InMemoryTransportOptions.DeferEnabled"/> — BEFORE calling this method, so a rejected
    /// settlement call never claims the entry: the delivery stays in flight and can still be settled by
    /// another (valid) call, or reclaimed later by the runner's own cleanup or the shutdown sweep. Once a
    /// call reaches this method its action is known-valid, and this is the first potentially-failing step
    /// — settlement never checks the caller's cancellation token, here or anywhere else, because it never
    /// waits for anything. A <see langword="false"/> result means the delivery was already requeued or
    /// dropped by its runner's cleanup or the shutdown sweep: settlement is then a no-op (no slot release,
    /// no buffer access). The message owns its buffer: an acknowledgement never touches it, and any path
    /// that puts the delivery back on a queue copies the body.
    /// </remarks>
    /// <param name="message">The inbound message being settled.</param>
    /// <param name="entry">The claimed entry, when found.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    internal bool TryTakeInFlight(InboundMessage message, out InFlightDelivery entry)
    {
        ArgumentNullException.ThrowIfNull(message);
        return InFlight.TryTake(message.DeliveryTag, out entry);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Waits only for queues with an active consumer (<see cref="InMemoryQueue.HasActiveConsumer"/>) to
    /// reach zero <see cref="InMemoryQueue.Occupancy"/> — a dead-letter queue, or any other queue nobody
    /// is currently consuming from, is skipped, because nothing would ever drain it. The set of active
    /// queues is re-evaluated on every poll, not fixed at the start of the call. A delivery a consumer
    /// disposed without settling it — its buffer already back in the pool — is claimed and its slot
    /// released on every iteration (see <see cref="SweepReleasedByConsumer"/>), so it never strands the
    /// drain until the timeout the way it would if only a runner's own end-of-enumeration cleanup ever
    /// noticed it. A delivery scheduled for a deferred redelivery still holds its queue's slot for as
    /// long as it is pending, so it still counts toward that queue's occupancy.
    /// </para>
    /// <para>
    /// Elapsing <paramref name="timeout"/> is a normal, non-throwing return — messages still queued at
    /// that point are not delivered (at-most-once), matching this adapter's own <c>closed</c> semantics
    /// elsewhere. Cancelling <paramref name="cancellationToken"/> instead throws
    /// <see cref="OperationCanceledException"/>. Returns an already-completed <see cref="Task"/>, without
    /// allocating a <see cref="CancellationTokenSource"/> or a timer, whenever every active queue is
    /// already drained — including once this adapter has been disposed.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative and not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout,
                "The drain timeout must not be negative, unless it is Timeout.InfiniteTimeSpan (wait indefinitely).");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();

        SweepReleasedByConsumer();
        return IsDrained() ? Task.CompletedTask : DrainSlowAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// The polling path <see cref="DrainAsync"/> falls back to once the fast, synchronous check finds at
    /// least one active queue not yet drained. Polls every <see cref="DrainPollInterval"/>, sweeping
    /// <see cref="InFlight"/> and re-checking <see cref="IsDrained"/> after each wait, until either every
    /// active queue is drained, <paramref name="timeout"/> elapses, <paramref name="cancellationToken"/>
    /// is cancelled, or this adapter is disposed while the wait is in progress.
    /// </summary>
    private async Task DrainSlowAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        TimeSpan clamped = timeout > InMemoryQueue.MaxSupportedWaitTimeout
            ? InMemoryQueue.MaxSupportedWaitTimeout
            : timeout;

        async Task PollUntilDrainedAsync(CancellationToken pollToken)
        {
            while (true)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                await Task.Delay(DrainPollInterval, _timeProvider, pollToken).ConfigureAwait(false);

                SweepReleasedByConsumer();
                if (IsDrained())
                {
                    return;
                }
            }
        }

        if (clamped == Timeout.InfiniteTimeSpan)
        {
            await PollUntilDrainedAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = new CancellationTokenSource(clamped, _timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await PollUntilDrainedAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The timeout elapsed, not the caller: a normal, non-throwing return (at-most-once) —
            // matching IGracefulDrainTransport's contract. Cancelling cancellationToken itself is not
            // caught here and propagates as OperationCanceledException, as documented on DrainAsync.
        }
    }

    /// <summary>
    /// Claims every entry of <see cref="InFlight"/> whose message was disposed by its consumer without
    /// being settled and releases its queue slot, so such a delivery never strands
    /// <see cref="DrainAsync"/> until its timeout. Two phases, in order: every claimed entry's slot is
    /// released first, and only once every one of them has been released is the per-queue count reported
    /// to <see cref="_diagnostics"/> — so a throwing diagnostics listener can never leave a claimed entry
    /// with its slot still held, which would otherwise leak occupancy that no later drain could ever
    /// observe being freed.
    /// </summary>
    private void SweepReleasedByConsumer()
    {
        List<InFlightDelivery> released = InFlight.TakeReleasedByConsumer();
        if (released.Count == 0)
        {
            return;
        }

        var perQueue = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (InFlightDelivery entry in released)
        {
            entry.Queue.ReleaseSlot();
            perQueue[entry.Queue.Name] = perQueue.GetValueOrDefault(entry.Queue.Name) + 1;
        }

        foreach (KeyValuePair<string, int> dropped in perQueue)
        {
            _diagnostics.DeliveriesDisposedUnsettled(dropped.Key, dropped.Value);
        }
    }

    /// <summary>
    /// Gets whether every queue with an active consumer currently has zero <see cref="InMemoryQueue.Occupancy"/>.
    /// A queue nobody is consuming from (a dead-letter queue included) never affects this result.
    /// </summary>
    private bool IsDrained()
    {
        foreach (InMemoryQueue queue in Broker.Queues)
        {
            if (queue.HasActiveConsumer && queue.Occupancy != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Shuts the adapter down: closes every queue — waking any sender still waiting for room with
    /// <c>closed</c> instead of leaving it to wait out its timeout — stops every running consume
    /// enumeration, cancels every pending deferred redelivery (see <see cref="InMemoryDeferScheduler.Dispose"/>),
    /// and drops every delivery still sitting in a queue's channel or still handed to a consumer and
    /// unsettled, releasing its queue slot and returning its buffer to <see cref="BufferPool"/>, with one
    /// aggregated warning log and one metric measurement per affected queue. Idempotent.
    /// </summary>
    /// <remarks>
    /// Every buffer this adapter is still holding on to at the moment it is disposed — sitting in a
    /// queue's channel, or handed to a consumer and never settled — is returned to <see cref="BufferPool"/>
    /// exactly once before this method returns (or, if a diagnostics listener itself throws, before that
    /// exception propagates): every queue is closed and drained, and every in-flight delivery is released,
    /// entirely before this method reports what was dropped. A <c>Requeue</c> or dead-letter write that
    /// races this call and reaches a queue's channel a moment too late is dropped by that queue on arrival
    /// the same way, and counted toward the same per-queue total — see the similar note on
    /// <see cref="SendBatchAsync"/>.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();

        ImmutableArray<InMemoryQueue> queues = Broker.Queues;

        foreach (InMemoryQueue queue in queues)
        {
            queue.Close(BufferPool);
        }

        foreach (InMemoryQueue queue in queues)
        {
            queue.DropRemaining();
        }

        _deferScheduler?.Dispose();

        var droppedOnShutdownPerQueue = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (InFlightDelivery entry in InFlight.TakeAll())
        {
            entry.Queue.ReleaseSlot();
            droppedOnShutdownPerQueue[entry.Queue.Name] =
                droppedOnShutdownPerQueue.GetValueOrDefault(entry.Queue.Name) + 1;
        }

        foreach (KeyValuePair<string, int> dropped in droppedOnShutdownPerQueue)
        {
            _diagnostics.DeliveriesDroppedOnShutdown(dropped.Key, dropped.Value);
        }

        foreach (InMemoryQueue queue in queues)
        {
            _diagnostics.DeliveriesDroppedOnDrain(queue.Name, queue.TakeDroppedAfterClose());
        }
    }

    /// <inheritdoc cref="Dispose"/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Mirrors RabbitMQ's settlement map: <see cref="SettlementAction.Ack"/> only releases the source
    /// queue's slot (the message's own <see cref="InboundMessage.Dispose"/> returns its buffer);
    /// <see cref="SettlementAction.Nack"/> and <see cref="SettlementAction.Reject"/> dead-letter (a
    /// declared <c>x-dead-letter-exchange</c> on the source queue, or the delivery is dropped with a
    /// <see cref="LogLevel.Warning"/> log and a counter); <see cref="SettlementAction.Requeue"/> puts a
    /// copy back at the head of the source queue with an incremented redelivery count, unless
    /// <see cref="InMemoryTransportOptions.MaxRedeliveries"/> is already reached, in which case it
    /// dead-letters instead. <see cref="SettlementAction.Defer"/> throws <see cref="NotSupportedException"/>
    /// when <see cref="InMemoryTransportOptions.DeferEnabled"/> is off; when it is on, a copy is scheduled
    /// for write-back to the same, already-reserved slot after <see cref="InMemoryTransportOptions.DeferDelay"/>
    /// — never checked against <see cref="InMemoryTransportOptions.MaxRedeliveries"/>. Configuring
    /// <c>EnableDefer()</c> together with a receive endpoint that declares per-key ordering is rejected at
    /// startup, since a deferred redelivery would otherwise reorder that endpoint's stream.
    /// </para>
    /// <para>
    /// This method is never <see langword="async"/> and never waits for anything — it always returns an
    /// already-completed <see cref="Task"/>, and <paramref name="cancellationToken"/> is never checked.
    /// Argument and action validation happens before the delivery is claimed from <see cref="InFlight"/>;
    /// see <see cref="TryTakeInFlight"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not a known <see cref="SettlementAction"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="action"/> is <see cref="SettlementAction.Defer"/> and
    /// <see cref="InMemoryTransportOptions.DeferEnabled"/> is off.
    /// </exception>
    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (action)
        {
            case SettlementAction.Ack:
            case SettlementAction.Nack:
            case SettlementAction.Reject:
            case SettlementAction.Requeue:
                break;

            case SettlementAction.Defer when !Options.DeferEnabled:
                throw new NotSupportedException(
                    "The in-memory transport operation 'Defer' requires EnableDefer() on the in-memory " +
                    "configurator. The delivery was left in flight and can still be settled with another action.");

            case SettlementAction.Defer:
                // DeferEnabled is on: falls through to the common claim-and-settle path below, exactly
                // like Ack/Nack/Reject/Requeue. _settlement.Settle routes it to _deferScheduler, which is
                // guaranteed non-null whenever DeferEnabled is on (see the constructor).
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown settlement action.");
        }

        if (!TryTakeInFlight(message, out InFlightDelivery entry))
        {
            return Task.CompletedTask;
        }

        _settlement.Settle(action, entry);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The in-memory topology is sealed at construction and cannot change afterwards. A declaration
    /// that is reference-equal to <see cref="InMemoryTransportOptions.Topology"/> (the instance the
    /// registry was built from) or structurally equivalent to it is an idempotent no-op. Any other
    /// declaration is rejected: an unsupported exchange type throws
    /// <see cref="BareWireTransportException"/>; any other difference — including a queue argument the
    /// in-memory transport does not support, such as adding a TTL to a queue that had none at build
    /// time — throws <see cref="BareWireConfigurationException"/>, because the frozen-topology check
    /// always takes precedence over the unsupported-argument check on this path.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="topology"/> is <see langword="null"/>.</exception>
    /// <exception cref="BareWireTransportException">
    /// Thrown when <paramref name="topology"/> declares an exchange with
    /// <see cref="ExchangeType.Headers"/> or <see cref="ExchangeType.ConsistentHash"/>.
    /// </exception>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <paramref name="topology"/> is not identical to the topology sealed when this
    /// transport was built.
    /// </exception>
    public Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        cancellationToken.ThrowIfCancellationRequested();

        if (ReferenceEquals(topology, Options.Topology))
        {
            return Task.CompletedTask;
        }

        InMemoryTopologyInterpreter.ThrowIfUnsupportedExchangeTypes(topology);

        NormalizedTopology normalized = InMemoryTopologyInterpreter.Normalize(topology);
        if (!normalized.IsEquivalentTo(Registry.Declared))
        {
            throw new BareWireConfigurationException(
                optionName: "DeployTopologyAsync",
                optionValue:
                    $"{normalized.Exchanges.Count} exchanges, {normalized.Queues.Count} queues, " +
                    $"{normalized.ExchangeQueueBindings.Count + normalized.ExchangeExchangeBindings.Count} bindings",
                expectedValue:
                    "a declaration identical to the topology sealed when the transport was built " +
                    "(the in-memory topology cannot change at runtime)");
        }

        return Task.CompletedTask;
    }
}
