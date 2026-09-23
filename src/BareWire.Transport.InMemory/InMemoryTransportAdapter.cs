using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory.Internal;

namespace BareWire.Transport.InMemory;

/// <summary>
/// Skeleton in-memory transport adapter. <see cref="Capabilities"/> is
/// <see cref="TransportCapabilities.None"/> and every I/O member throws
/// <see cref="NotSupportedException"/> — the routing, queueing, and settlement behaviour is added by
/// later subtasks. Registered as an <see cref="ITransportAdapter"/> DI singleton by
/// <c>AddBareWireInMemory</c>.
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

    /// <inheritdoc />
    public Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The in-memory transport operation 'SendBatchAsync' is not implemented yet.");

    /// <inheritdoc />
    public IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The in-memory transport operation 'ConsumeAsync' is not implemented yet.");

    /// <inheritdoc />
    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The in-memory transport operation 'SettleAsync' is not implemented yet.");

    /// <inheritdoc />
    public Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The in-memory transport operation 'DeployTopologyAsync' is not implemented yet.");
}
