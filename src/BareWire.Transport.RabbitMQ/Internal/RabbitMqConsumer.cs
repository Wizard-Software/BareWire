using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BareWire.Abstractions.Transport;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BareWire.Transport.RabbitMQ.Internal;

/// <summary>
/// Bridges RabbitMQ deliveries into a bounded <see cref="Channel{T}"/> of <see cref="InboundMessage"/>.
/// Inherits from <see cref="AsyncDefaultBasicConsumer"/> which provides default no-op implementations
/// for all <see cref="IAsyncBasicConsumer"/> methods.
/// </summary>
internal sealed class RabbitMqConsumer : AsyncDefaultBasicConsumer
{
    private readonly Channel<InboundMessage> _inboundChannel;
    private readonly RabbitMqHeaderMapper _headerMapper;
    private readonly string _consumerChannelId;
    private readonly object _cancelLock = new();

    /// <summary>
    /// Upper bound, in milliseconds, for each broker round trip made while a consumer shuts down
    /// (<c>basic.cancel</c> and the requeue nacks of the drain). Not configurable on purpose: it only has to
    /// keep a stuck broker from consuming the host's shutdown budget.
    /// </summary>
    internal const int ShutdownStepTimeoutMilliseconds = 5000;

    private volatile bool _stopRequested;
    private volatile bool _writerCompleted;
    private Task<bool>? _cancelTask;
    private int _cancelFailureReported;

    internal RabbitMqConsumer(
        IChannel channel,
        Channel<InboundMessage> inboundChannel,
        RabbitMqHeaderMapper headerMapper,
        string consumerChannelId)
        : base(channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(inboundChannel);
        ArgumentNullException.ThrowIfNull(headerMapper);
        ArgumentException.ThrowIfNullOrEmpty(consumerChannelId);

        _inboundChannel = inboundChannel;
        _headerMapper = headerMapper;
        _consumerChannelId = consumerChannelId;
    }

    /// <summary>The broker-assigned consumer tag; set by the adapter once <c>basic.consume</c> returns.</summary>
    internal string AssignedTag { get; set; } = string.Empty;

    /// <summary>
    /// <see langword="true"/> once the caller's cancellation token fired. Set synchronously from the token
    /// registration so a requeue settled by the runner can cancel the AMQP consumer first.
    /// </summary>
    internal bool IsStopRequested => _stopRequested;

    /// <summary>Marks the consumer as stopping; called from the caller's token registration.</summary>
    internal void RequestStop() => _stopRequested = true;

    /// <summary>
    /// Completes the inbound buffer for writing. Deliveries that arrive afterwards are not buffered.
    /// </summary>
    internal void CompleteWriter()
    {
        _writerCompleted = true;
        _inboundChannel.Writer.TryComplete();
    }

    /// <summary>
    /// Sends <c>basic.cancel</c> for this consumer exactly once and waits (bounded by
    /// <see cref="ShutdownStepTimeoutMilliseconds"/>) for cancel-ok. Single-flight: the caller that wins the
    /// race starts the RPC, every other caller awaits the same task, and the outcome is remembered even when
    /// the RPC failed or timed out (it is never retried). Never throws.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the broker confirmed the cancel; <see langword="false"/> when it was skipped
    /// (channel not open) or failed, in which case the caller must not assume deliveries have stopped.
    /// </returns>
    internal Task<bool> EnsureCancelledAsync()
    {
        lock (_cancelLock)
        {
            return _cancelTask ??= CancelCoreAsync();
        }
    }

    /// <summary>The failure of the one-shot cancel, if it failed; <see langword="null"/> otherwise.</summary>
    internal Exception? CancelFailure { get; private set; }

    /// <summary>
    /// Returns <see langword="true"/> for the first caller only, so a failed cancel is logged once even though
    /// several settlements can observe it.
    /// </summary>
    internal bool TryClaimCancelFailureReport() => Interlocked.Exchange(ref _cancelFailureReported, 1) == 0;

    private async Task<bool> CancelCoreAsync()
    {
        if (!Channel.IsOpen || AssignedTag.Length == 0)
        {
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(ShutdownStepTimeoutMilliseconds);
            await Channel.BasicCancelAsync(AssignedTag, noWait: false, cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Handled by recording: the caller reads CancelFailure and falls back to closing the channel.
            CancelFailure = ex;
            return false;
        }
    }

    /// <summary>
    /// Yields buffered deliveries, checking the caller's token before every read so a cancelled consumer never
    /// takes another message out of the buffer (the BCL <c>ReadAllAsync</c> only checks it while waiting).
    /// </summary>
    internal async IAsyncEnumerable<InboundMessage> ReadUntilCancelledAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ChannelReader<InboundMessage> reader = _inboundChannel.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (!cancellationToken.IsCancellationRequested && reader.TryRead(out InboundMessage? message))
            {
                yield return message;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Outcome of <see cref="DrainAndRequeueAsync"/>.
    /// </summary>
    /// <param name="Drained">Deliveries removed from the buffer and disposed.</param>
    /// <param name="Requeued">Deliveries successfully nacked with requeue.</param>
    /// <param name="SkippedNacks">
    /// Deliveries disposed without a nack after the first failure or a closed channel.
    /// </param>
    /// <param name="FirstFailure">The first nack failure, if any.</param>
    /// <param name="FirstFailedTag">Delivery tag of the first failed nack.</param>
    /// <param name="FirstFailedMessageId">Message id of the first failed nack.</param>
    internal readonly record struct DrainResult(
        int Drained,
        int Requeued,
        int SkippedNacks,
        Exception? FirstFailure,
        ulong FirstFailedTag,
        string? FirstFailedMessageId);

    /// <summary>
    /// Hands every delivery still sitting in the buffer back to the broker (nack, <c>requeue: true</c>) and
    /// returns its pooled buffer. Call it after the AMQP consumer was cancelled and the writer completed.
    /// </summary>
    /// <remarks>
    /// Scope: only deliveries that were never handed to the runner. They never received
    /// <c>FlowController</c> credits (credits are granted after the read), so none are released here; releasing
    /// them would double-release. Nacks use <c>multiple: false</c> because <c>multiple: true</c> would also
    /// settle in-flight deliveries owned by the runner. Each message is disposed BEFORE its nack so a failing
    /// or slow broker call cannot leak its pooled buffer. After the first nack failure (or when the channel is
    /// closed) the drain switches to dispose-only: the broker returns unsettled deliveries itself when the
    /// channel closes. This method never throws.
    /// Requeue vs dispose-only (measured on rabbitmq:4.2 quorum): both raise <c>x-delivery-count</c> of a
    /// buffered delivery by exactly 1, so the explicit nack-requeue chosen by the maintainers is kept; it also
    /// returns the messages immediately instead of waiting for the channel to close.
    /// </remarks>
    internal async Task<DrainResult> DrainAndRequeueAsync(CancellationToken cancellationToken)
    {
        int drained = 0;
        int requeued = 0;
        int skipped = 0;
        Exception? firstFailure = null;
        ulong firstFailedTag = 0;
        string? firstFailedMessageId = null;
        bool nackMode = true;

        while (_inboundChannel.Reader.TryRead(out InboundMessage? message))
        {
            ulong tag = message.DeliveryTag;
            string messageId = message.MessageId;
            message.Dispose();
            drained++;

            if (nackMode && !Channel.IsOpen)
            {
                nackMode = false;
            }

            if (!nackMode)
            {
                skipped++;
                continue;
            }

            try
            {
                await Channel.BasicNackAsync(tag, multiple: false, requeue: true, cancellationToken)
                    .ConfigureAwait(false);
                requeued++;
            }
            catch (Exception ex)
            {
                // Handled by recording: switch to dispose-only and surface one aggregated warning.
                nackMode = false;
                firstFailure = ex;
                firstFailedTag = tag;
                firstFailedMessageId = messageId;
            }
        }

        return new DrainResult(drained, requeued, skipped, firstFailure, firstFailedTag, firstFailedMessageId);
    }

    /// <inheritdoc />
    public override async Task HandleBasicDeliverAsync(
        string consumerTag,
        ulong deliveryTag,
        bool redelivered,
        string exchange,
        string routingKey,
        IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        // CRITICAL: RabbitMQ.Client frees the body memory after this handler returns.
        // We MUST copy the bytes before writing to the channel so consumers receive stable data.
        // Rent from ArrayPool to avoid per-message heap allocation (ADR-003 zero-copy).
        byte[]? pooledBuffer = null;
        ReadOnlySequence<byte> bodySequence;
        if (body.Length == 0)
        {
            bodySequence = ReadOnlySequence<byte>.Empty;
        }
        else
        {
            pooledBuffer = ArrayPool<byte>.Shared.Rent(body.Length);
            body.Span.CopyTo(pooledBuffer);
            bodySequence = new ReadOnlySequence<byte>(pooledBuffer, 0, body.Length);
        }

        Dictionary<string, string> headers = _headerMapper.MapInbound(properties);

        // Add endpoint routing information so SettleAsync can resolve the channel.
        // These are internal BareWire headers — added AFTER the mapper so they are not subject
        // to any custom mapping or passthrough filtering.
        headers["BW-RoutingKey"] = routingKey;
        headers["BW-Exchange"] = exchange;
        headers["BW-ConsumerChannelId"] = _consumerChannelId;

        string messageId = headers.TryGetValue("message-id", out string? mappedId) && !string.IsNullOrEmpty(mappedId)
            ? mappedId
            : Guid.NewGuid().ToString();

        InboundMessage message = new(
            messageId: messageId,
            headers: headers,
            body: bodySequence,
            deliveryTag: deliveryTag,
            pooledBuffer: pooledBuffer);

        // TryWrite returns false when the buffer is full or when the writer is completed.
        // The pooled buffer must be returned here since ReceiveEndpointRunner will never see this message.
        if (!_inboundChannel.Writer.TryWrite(message))
        {
            message.Dispose();

            if (_writerCompleted || _inboundChannel.Reader.Completion.IsCompleted)
            {
                // The consumer stopped reading and the broker still pushes to it: basic.cancel was skipped
                // (channel not open) or failed. Nack-requeue here would make the broker hand the same
                // message straight back, an unbounded broker -> nack -> broker loop. Leave the delivery
                // unacknowledged instead; the adapter closes the channel in that case and the broker then
                // returns it to the queue exactly once.
                return;
            }

            // Buffer full: nack-requeue so the broker can redeliver it later or to another consumer.
            await Channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task HandleChannelShutdownAsync(object channel, ShutdownEventArgs reason)
    {
        CompleteWriter();
        return Task.CompletedTask;
    }
}
