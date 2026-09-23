using System.Collections.Frozen;
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
/// remarks; settling messages is added by a later subtask and still throws
/// <see cref="NotSupportedException"/>. Registered as an <see cref="ITransportAdapter"/> DI singleton
/// by <c>AddBareWireInMemory</c>.
/// </summary>
internal sealed class InMemoryTransportAdapter : ITransportAdapter, IDisposable, IAsyncDisposable
{
    private readonly FrozenDictionary<string, SemaphoreSlim> _singleActiveGates;
    private readonly InMemoryConsumeDiagnostics _diagnostics;
    private readonly InMemorySendDiagnostics _sendDiagnostics;
    private readonly InMemorySender _sender;

    // Never disposed: a runner's finally block may still observe it after shutdown. A cancellation token
    // source without a timer holds no unmanaged resource.
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <param name="options">The transport options the adapter was configured with.</param>
    /// <param name="broker">The container-scoped broker owning the queues.</param>
    /// <param name="logger">An optional logger; defaults to a no-op logger.</param>
    /// <param name="meter">An optional meter for the consume- and send-path counters; no instruments when omitted.</param>
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
        Meter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(broker);

        ILogger effectiveLogger = logger ?? (ILogger)NullLogger<InMemoryTransportAdapter>.Instance;

        // Built first, so an unsupported affinity fails before the topology registry is built.
        _singleActiveGates = BuildSingleActiveGates(options);
        _diagnostics = new InMemoryConsumeDiagnostics(effectiveLogger, meter);

        Options = options;
        Broker = broker;
        Registry = AttachRegistry(broker, InMemoryTopologyInterpreter.BuildRegistry(options));
        InFlight = new InMemoryDeliveryMap();

        Router = new InMemoryRouter(Registry, options, new DelegatingLogger<InMemoryRouter>(effectiveLogger), meter);
        _sendDiagnostics = new InMemorySendDiagnostics(effectiveLogger, Registry, meter);
        _sender = new InMemorySender(broker, Router, options, _sendDiagnostics, IsClosed);
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

    /// <summary>Gets the number of unsettled deliveries dropped because the adapter shut down.</summary>
    internal long DroppedOnShutdownCount => _diagnostics.DroppedOnShutdownCount;

    /// <summary>Gets the total number of rejected copies/messages recorded by the send path so far.</summary>
    internal long SendRejectedCount => _sendDiagnostics.RejectedCount;

    /// <summary>
    /// Gets the number of deliveries dropped because the consumer disposed their messages without settling
    /// them, so their bodies could not be requeued.
    /// </summary>
    internal long DisposedUnsettledCount => _diagnostics.DisposedUnsettledCount;

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
    /// Once this adapter is disposed, this call — and any later message of a call already in flight —
    /// reports <see langword="false"/> with reason <c>closed</c> instead of throwing
    /// <see cref="ObjectDisposedException"/>. A write that races disposal and reaches a queue's channel a
    /// moment too late leaves an unclaimed pooled buffer behind; reclaiming it is left to a future
    /// graceful-drain subtask, not this one.
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
        var runner = new InMemoryQueueRunner(queue, InFlight, gate, _diagnostics, _shutdown.Token);
        return runner.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Claims the in-flight entry of <paramref name="message"/>, removing it from <see cref="InFlight"/>.
    /// The minimal lookup settlement builds on.
    /// </summary>
    /// <remarks>
    /// Settlement must call this first — before any cancellation check or validation that could throw —
    /// because the consume loop may settle with an already cancelled token during shutdown. A
    /// <see langword="false"/> result means the delivery was already requeued or dropped by its runner's
    /// cleanup or the shutdown sweep: settlement is then a no-op (no slot release, no buffer access). The
    /// message owns its buffer: an acknowledgement never touches it, and any path that puts the delivery
    /// back on a queue copies the body.
    /// </remarks>
    /// <param name="message">The inbound message being settled.</param>
    /// <param name="entry">The claimed entry, when found.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    internal bool TryTakeInFlight(InboundMessage message, out InFlightDelivery entry)
    {
        ArgumentNullException.ThrowIfNull(message);
        return InFlight.TryTake(message.DeliveryTag, out entry);
    }

    /// <summary>
    /// Shuts the adapter down: stops every running consume enumeration and drops every delivery still in
    /// flight, releasing its queue slot (never its buffer, which belongs to the message), with one warning
    /// log and one counter increment per affected queue. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();

        List<InFlightDelivery> remaining = InFlight.TakeAll();
        if (remaining.Count == 0)
        {
            return;
        }

        var droppedPerQueue = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (InFlightDelivery entry in remaining)
        {
            entry.Queue.ReleaseSlot();
            droppedPerQueue[entry.Queue.Name] = droppedPerQueue.GetValueOrDefault(entry.Queue.Name) + 1;
        }

        foreach (KeyValuePair<string, int> dropped in droppedPerQueue)
        {
            _diagnostics.DeliveriesDroppedOnShutdown(dropped.Key, dropped.Value);
        }
    }

    /// <inheritdoc cref="Dispose"/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The in-memory transport operation 'SettleAsync' is not implemented yet.");

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
