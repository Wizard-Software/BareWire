using BareWire.Abstractions.Topology;

namespace BareWire.Abstractions.Transport;

/// <summary>
/// Abstracts all transport-layer I/O operations, decoupling the BareWire core pipeline from
/// any specific broker implementation (e.g. RabbitMQ, Azure Service Bus).
/// Implementations are resolved as DI singletons and must be thread-safe.
/// </summary>
public interface ITransportAdapter
{
    /// <summary>
    /// Gets the human-readable name of the underlying transport (e.g. <c>"RabbitMQ"</c>).
    /// Used for diagnostic messages, health checks, and logging.
    /// </summary>
    string TransportName { get; }

    /// <summary>
    /// Gets the set of capabilities supported by this transport adapter.
    /// The core pipeline uses this to enable or disable optional features such as publisher confirms
    /// or exactly-once delivery.
    /// </summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Sends a batch of outbound messages to the transport in a single operation where possible.
    /// The transport may split the batch internally if protocol limits are exceeded.
    /// </summary>
    /// <param name="messages">The list of messages to send. Must not be null or empty.</param>
    /// <param name="cancellationToken">A token to cancel the batch send operation.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that resolves to one <see cref="SendResult"/> per message,
    /// in the same order as <paramref name="messages"/>.
    /// </returns>
    Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an asynchronous stream of inbound messages from the specified endpoint,
    /// applying the given flow-control options to bound concurrency and memory usage.
    /// The stream continues until the <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="endpointName">The transport queue or topic name to consume from.</param>
    /// <param name="flowControl">The flow-control options that govern in-flight limits and back-pressure.</param>
    /// <param name="cancellationToken">A token to stop consuming and close the stream.</param>
    /// <returns>An <see cref="IAsyncEnumerable{T}"/> of inbound messages.</returns>
    IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles a previously consumed message by applying the specified <paramref name="action"/>
    /// (e.g. acknowledge, reject, requeue).
    /// </summary>
    /// <param name="action">The settlement decision for this message.</param>
    /// <param name="message">The inbound message to settle. Must not be null.</param>
    /// <param name="cancellationToken">A token to cancel the settlement operation.</param>
    /// <returns>A <see cref="Task"/> that completes when the broker has acknowledged the settlement.</returns>
    Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deploys the specified topology declarations (exchanges, queues, bindings) to the broker.
    /// This method is idempotent — it is safe to call multiple times.
    /// This is never invoked automatically; callers must trigger it explicitly (manual topology).
    /// </summary>
    /// <param name="topology">The topology to deploy. Must not be null.</param>
    /// <param name="cancellationToken">A token to cancel the deployment operation.</param>
    /// <returns>A <see cref="Task"/> that completes when all topology elements have been applied.</returns>
    /// <exception cref="System.Exception">
    /// Throws <c>TopologyDeploymentException</c> when the broker rejects or cannot apply
    /// one or more topology declarations.
    /// </exception>
    Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default);
}
