using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;

namespace BareWire.Testing;

/// <summary>
/// An in-memory implementation of <see cref="ITransportAdapter"/> intended for use in unit and
/// integration tests. Messages sent via <see cref="SendBatchAsync"/> are routed to per-endpoint
/// bounded channels and can be consumed via <see cref="ConsumeAsync"/>. No external broker is
/// required.
/// </summary>
/// <remarks>
/// All channels are bounded. The default capacity for send-side channel creation (when no
/// consumer has registered flow-control options yet) is <see cref="DefaultChannelCapacity"/>.
/// Disposal completes all channel writers, which causes any active <see cref="ConsumeAsync"/>
/// enumerators to terminate gracefully.
/// </remarks>
internal sealed class InMemoryTransportAdapter : ITransportAdapter, IConsumerChannelManager, IAsyncDisposable
{
    private const int DefaultChannelCapacity = 1_000;

    private readonly ConcurrentDictionary<string, Channel<InboundMessage>> _endpoints = new();
    private long _deliveryTagCounter;
    private bool _disposed;

    /// <summary>
    /// Raised after each <see cref="OutboundMessage"/> is processed in <see cref="SendBatchAsync"/>.
    /// Used by <c>BareWireTestHarness</c> to observe outbound messages without polling.
    /// </summary>
    internal event Action<OutboundMessage>? MessageSent;

    /// <inheritdoc />
    public string TransportName => "InMemory";

    /// <inheritdoc />
    public TransportCapabilities Capabilities => TransportCapabilities.None;

    /// <inheritdoc />
    public Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var results = new SendResult[messages.Count];

        for (int i = 0; i < messages.Count; i++)
        {
            OutboundMessage outbound = messages[i];
            string endpointName = outbound.RoutingKey;

            Channel<InboundMessage> channel = _endpoints.GetOrAdd(
                endpointName,
                static _ => Channel.CreateBounded<InboundMessage>(DefaultChannelCapacity));

            ulong deliveryTag = (ulong)Interlocked.Increment(ref _deliveryTagCounter);
            string messageId = Guid.NewGuid().ToString();

            // Convert ReadOnlyMemory<byte> → ReadOnlySequence<byte> (zero-copy wrapper).
            ReadOnlySequence<byte> body = outbound.Body.IsEmpty
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(outbound.Body);

            InboundMessage inbound = new(
                messageId: messageId,
                headers: outbound.Headers,
                body: body,
                deliveryTag: deliveryTag);

            // TryWrite is non-blocking. If the channel is full (capacity reached) the message is
            // dropped and the send is reported as unconfirmed. This matches the bounded-only
            // contract required by the project constitution.
            bool written = channel.Writer.TryWrite(inbound);
            results[i] = new SendResult(IsConfirmed: written, DeliveryTag: deliveryTag);

            MessageSent?.Invoke(outbound);
        }

        return Task.FromResult<IReadOnlyList<SendResult>>(results);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);

        Channel<InboundMessage> channel = _endpoints.GetOrAdd(
            endpointName,
            _ => Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                FullMode = flowControl.FullMode,
                SingleWriter = false,
                SingleReader = false,
            }));

        await foreach (InboundMessage message in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    /// <inheritdoc />
    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        // In-memory transport has no broker to confirm settlement with — always succeeds.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        // In-memory transport has no external broker — topology declarations are silently accepted.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseConsumerChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        // In-memory transport has no broker channels to release — no-op.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Completes all channel writers, causing any active <see cref="ConsumeAsync"/> streams to
    /// terminate after draining any remaining messages.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        foreach (Channel<InboundMessage> channel in _endpoints.Values)
        {
            channel.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}
