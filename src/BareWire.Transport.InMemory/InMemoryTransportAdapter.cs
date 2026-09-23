using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory.Internal;
using BareWire.Transport.InMemory.Topology;

namespace BareWire.Transport.InMemory;

/// <summary>
/// In-memory transport adapter. At construction it interprets the configured
/// <see cref="InMemoryTransportOptions.Topology"/> (plus receive-endpoint queue names and per-type
/// exchange mappings) into a sealed <see cref="Registry"/> — an invalid configuration throws
/// <see cref="BareWireConfigurationException"/> here, at bus build time, rather than on the wire. An
/// exchange type or queue argument the in-memory transport does not support throws
/// <see cref="BareWireTransportException"/> at the same point. Once built, the topology is frozen:
/// <see cref="DeployTopologyAsync"/> accepts only the same declaration (idempotent no-op) and rejects
/// any other one, and <see cref="ConsumeAsync"/> refuses to start on an undeclared queue. Sending,
/// consuming, and settling messages are added by later subtasks and still throw
/// <see cref="NotSupportedException"/>. Registered as an <see cref="ITransportAdapter"/> DI singleton
/// by <c>AddBareWireInMemory</c>.
/// </summary>
internal sealed class InMemoryTransportAdapter(InMemoryTransportOptions options, InMemoryBroker broker)
    : ITransportAdapter
{
    /// <inheritdoc />
    public string TransportName => "InMemory";

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.None;

    internal InMemoryTransportOptions Options { get; } = options;

    internal InMemoryBroker Broker { get; } = broker;

    /// <summary>
    /// Gets the sealed topology registry built from <see cref="Options"/> at construction time. Never
    /// mutated afterwards — see <see cref="InMemoryTopologyInterpreter.BuildRegistry"/>. Attached to
    /// <see cref="Broker"/> immediately after it is built, so the broker's queues exist by the time
    /// construction completes.
    /// </summary>
    internal ExchangeRegistry Registry { get; } = AttachRegistry(broker, InMemoryTopologyInterpreter.BuildRegistry(options));

    private static ExchangeRegistry AttachRegistry(InMemoryBroker broker, ExchangeRegistry registry)
    {
        broker.AttachRegistry(registry);
        return registry;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The in-memory transport operation 'SendBatchAsync' is not implemented yet.");

    /// <inheritdoc />
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

        if (!Registry.ContainsQueue(endpointName))
        {
            throw new BareWireConfigurationException(
                optionName: "ReceiveEndpoint",
                optionValue: endpointName,
                expectedValue: "a queue declared via ConfigureTopology, or AutoDeclareEndpointQueues() enabled");
        }

        throw new NotSupportedException(
            "The in-memory transport operation 'ConsumeAsync' is not implemented yet.");
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
