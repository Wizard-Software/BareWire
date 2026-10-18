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

    private static readonly TimeSpan MaxTimerDueTime = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private volatile bool _stopRequested;
    private volatile bool _writerCompleted;
    private Task<bool>? _cancelTask;
    private int _cancelFailureReported;

    // Highest delivery tag seen so far. Written only by the delivery dispatcher (the adapter pins
    // consumerDispatchConcurrency to 1), read from the shutdown handler through Volatile.
    private ulong _maxSeenDeliveryTag;

    // Every tag at or below this value was issued by a channel instance that has since shut down. Raised
    // monotonically by HandleChannelShutdownAsync; never lowered.
    private ulong _staleTagWatermark;
    private int _discardedStale;

    // TimeProvider timestamp of the first recoverable channel shutdown of the current series; 0 while the consumer
    // is not waiting for recovery. Read lock-free (health checks, the delivery hot path); every transition happens
    // under _recoveryLock, together with the timer that reports the wait once it exceeds RecoveryHealthTimeout.
    private long _awaitingRecoverySince;
    private readonly object _recoveryLock = new();
    private ITimer? _recoveryOverdueTimer;
    private bool _recoveryOverdueReported;

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

    /// <summary>The endpoint (queue) name this consumer reads from; used in channel release diagnostics.</summary>
    internal string EndpointName { get; init; } = string.Empty;

    /// <summary>
    /// Whether the connection factory recovers channels automatically (mirrors
    /// <c>RabbitMqTransportOptions.AutomaticRecoveryEnabled</c>).
    /// </summary>
    internal bool AutomaticRecoveryEnabled { get; init; }

    /// <summary>
    /// Reports whether the connection that owns this consumer's channel is still open. <see langword="null"/>
    /// means "unknown" and makes every channel shutdown terminal.
    /// </summary>
    internal Func<bool>? IsConnectionOpen { get; init; }

    /// <summary>Clock used to measure how long the consumer waits for automatic recovery.</summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// How long the consumer may wait for automatic recovery before the wait is reported (see
    /// <see cref="IsRecoveryOverdue"/>, <see cref="RecoveryOverdue"/>). <see cref="Timeout.InfiniteTimeSpan"/>
    /// disables the report.
    /// </summary>
    internal TimeSpan RecoveryHealthTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Invoked once per shutdown series, from a timer, when the wait for recovery reaches
    /// <see cref="RecoveryHealthTimeout"/>. Receives the time waited so far. Must not throw.
    /// </summary>
    internal Action<RabbitMqConsumer, TimeSpan>? RecoveryOverdue { get; init; }

    /// <summary>
    /// Invoked when the consumer is recovered after <see cref="RecoveryOverdue"/> was raised. Receives the total
    /// time waited. Must not throw.
    /// </summary>
    internal Action<RabbitMqConsumer, TimeSpan>? RecoveredAfterOverdue { get; init; }

    /// <summary>
    /// <see langword="true"/> while the consumer waits for the client to recover its channel and re-register it
    /// (a recoverable channel shutdown was seen and neither <c>consume-ok</c> nor a fresh delivery followed).
    /// </summary>
    /// <param name="waited">Time since the first shutdown of the current series.</param>
    internal bool TryGetRecoveryWait(out TimeSpan waited)
    {
        long since = Volatile.Read(ref _awaitingRecoverySince);
        if (since == 0)
        {
            waited = TimeSpan.Zero;
            return false;
        }

        waited = TimeProvider.GetElapsedTime(since);
        return true;
    }

    /// <summary>
    /// <see langword="true"/> when the consumer has waited for recovery for at least
    /// <see cref="RecoveryHealthTimeout"/>.
    /// </summary>
    /// <param name="waited">Time since the first shutdown of the current series.</param>
    internal bool IsRecoveryOverdue(out TimeSpan waited) =>
        TryGetRecoveryWait(out waited)
        && RecoveryHealthTimeout != Timeout.InfiniteTimeSpan
        && waited >= RecoveryHealthTimeout;

    /// <summary>
    /// Ends the wait for recovery without reporting a recovery; called when the consume loop ends.
    /// </summary>
    internal void StopRecoveryTracking() => EndRecoveryWait(recovered: false);

    /// <summary>
    /// <see langword="true"/> when <paramref name="deliveryTag"/> was issued by a channel instance that has since
    /// shut down, so the broker already returned the delivery to the queue.
    /// </summary>
    internal bool IsStale(ulong deliveryTag) => deliveryTag <= Volatile.Read(ref _staleTagWatermark);

    /// <summary>Number of buffered deliveries disposed without settlement because their channel shut down.</summary>
    internal int DiscardedStaleDeliveries => Volatile.Read(ref _discardedStale);

    /// <summary>The broker-assigned consumer tag; set by the adapter once <c>basic.consume</c> returns.</summary>
    internal string AssignedTag { get; set; } = string.Empty;

    /// <summary>
    /// <see langword="true"/> once the caller's cancellation token fired. Set synchronously from the token
    /// registration so a requeue settled by the runner can cancel the AMQP consumer first.
    /// </summary>
    internal bool IsStopRequested => _stopRequested;

    /// <summary>Marks the consumer as stopping; called from the caller's token registration.</summary>
    internal void RequestStop()
    {
        _stopRequested = true;

        // A stopping endpoint is not stuck waiting for recovery.
        EndRecoveryWait(recovered: false);
    }

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
                if (IsStale(message.DeliveryTag))
                {
                    // Raced in after the purge: the broker already returned this delivery, so the runner must
                    // never process (or settle) it.
                    DiscardStale(message);
                    continue;
                }

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
    /// <param name="DiscardedStale">Stale deliveries disposed without a nack.</param>
    internal readonly record struct DrainResult(
        int Drained,
        int Requeued,
        int SkippedNacks,
        Exception? FirstFailure,
        ulong FirstFailedTag,
        string? FirstFailedMessageId,
        int DiscardedStale);

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
    /// A stale delivery (its tag was issued by a channel that has since shut down, see <see cref="IsStale"/>) is
    /// only disposed, never nacked: the broker returned it when that channel closed, and the nack would target a
    /// recovered channel. This filter is defensive; the client already drops such nacks.
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
        int discardedStale = 0;
        bool nackMode = true;

        while (_inboundChannel.Reader.TryRead(out InboundMessage? message))
        {
            ulong tag = message.DeliveryTag;
            string messageId = message.MessageId;
            message.Dispose();
            drained++;

            if (IsStale(tag))
            {
                // Defensive: RabbitMQ.Client already ignores a nack for a tag of a recovered-away channel, and
                // the broker returned the delivery when that channel closed. Never nack it on the new channel.
                discardedStale++;
                Interlocked.Increment(ref _discardedStale);
                continue;
            }

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

        return new DrainResult(
            drained, requeued, skipped, firstFailure, firstFailedTag, firstFailedMessageId, discardedStale);
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

        // Track the highest tag before buffering so a shutdown handler running right after sees it.
        if (deliveryTag > Volatile.Read(ref _maxSeenDeliveryTag))
        {
            Volatile.Write(ref _maxSeenDeliveryTag, deliveryTag);
        }

        // A delivery of the recovered channel (its tags are offset above every tag of the dead one) proves the
        // consumer is registered again. One volatile read on the hot path; the lock is taken only while waiting.
        if (Volatile.Read(ref _awaitingRecoverySince) != 0 && !IsStale(deliveryTag))
        {
            EndRecoveryWait(recovered: true);
        }

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

            // Reaching this point means the "buffer >= prefetch" invariant is broken (the adapter sizes the
            // buffer to hold at least a full prefetch window). Kept as a defensive fallback: nack-requeue so the
            // broker can redeliver the message later or to another consumer.
            await Channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reacts to the shutdown of this consumer's channel. Every delivery buffered so far carries a tag of the
    /// dead channel and was already returned to the queue by the broker, so it is disposed without settlement
    /// and never reaches the runner.
    /// </summary>
    /// <remarks>
    /// When the client is going to recover the channel (see <see cref="IsRecoveryExpected"/>) the writer stays
    /// open: the client re-registers this very consumer and its deliveries, numbered above every old tag, keep
    /// flowing. Otherwise the shutdown is terminal and the writer is completed so the stream ends. If recovery
    /// never succeeds, or succeeds without re-registering the consumer, the stream simply waits for deliveries;
    /// stopping through the caller's token keeps working. The wait is timed (see <see cref="TryGetRecoveryWait"/>)
    /// so the adapter can report the endpoint as degraded. This method never throws.
    /// </remarks>
    public override Task HandleChannelShutdownAsync(object channel, ShutdownEventArgs reason)
    {
        ulong seen = Volatile.Read(ref _maxSeenDeliveryTag);
        ulong current;
        while ((current = Volatile.Read(ref _staleTagWatermark)) < seen
            && Interlocked.CompareExchange(ref _staleTagWatermark, seen, current) != current)
        {
        }

        if (IsRecoveryExpected(reason))
        {
            BeginRecoveryWait();
        }
        else
        {
            CompleteWriter();
            EndRecoveryWait(recovered: false);
        }

        DiscardStaleBuffered();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ends the wait for recovery: after recovery RabbitMQ.Client 7.2.1 re-registers this very consumer object
    /// (<c>basic.consume</c> with the recorded consumer), so the broker's <c>consume-ok</c> arrives here. When the
    /// consumer cannot be re-registered (for example its queue is gone) no <c>consume-ok</c> arrives and the wait
    /// goes on until <see cref="RecoveryHealthTimeout"/> reports it.
    /// </summary>
    public override async Task HandleBasicConsumeOkAsync(string consumerTag, CancellationToken cancellationToken = default)
    {
        await base.HandleBasicConsumeOkAsync(consumerTag, cancellationToken).ConfigureAwait(false);
        EndRecoveryWait(recovered: true);
    }

    /// <summary>
    /// Starts the wait for recovery unless one is already running: the first shutdown of a series sets the
    /// timestamp and arms the one-shot overdue timer.
    /// </summary>
    private void BeginRecoveryWait()
    {
        lock (_recoveryLock)
        {
            if (_awaitingRecoverySince != 0 || _stopRequested)
            {
                return;
            }

            long now = TimeProvider.GetTimestamp();
            Volatile.Write(ref _awaitingRecoverySince, now == 0 ? 1 : now);
            _recoveryOverdueReported = false;

            if (RecoveryHealthTimeout != Timeout.InfiniteTimeSpan)
            {
                // Timers accept at most uint.MaxValue - 1 milliseconds; a longer timeout is clamped (the health
                // check still compares against the full value).
                TimeSpan dueTime = RecoveryHealthTimeout < MaxTimerDueTime ? RecoveryHealthTimeout : MaxTimerDueTime;
                _recoveryOverdueTimer = TimeProvider.CreateTimer(
                    static state => ((RabbitMqConsumer)state!).OnRecoveryOverdueTimer(),
                    this,
                    dueTime,
                    Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnRecoveryOverdueTimer()
    {
        TimeSpan waited;
        lock (_recoveryLock)
        {
            long since = _awaitingRecoverySince;
            if (since == 0 || _recoveryOverdueReported)
            {
                return;
            }

            _recoveryOverdueReported = true;
            waited = TimeProvider.GetElapsedTime(since);
        }

        RecoveryOverdue?.Invoke(this, waited);
    }

    /// <summary>
    /// Ends the current wait (if any) and cancels the overdue timer. <paramref name="recovered"/> reports a
    /// recovery through <see cref="RecoveredAfterOverdue"/> when the wait had already been reported overdue.
    /// </summary>
    private void EndRecoveryWait(bool recovered)
    {
        TimeSpan waited;
        bool reportRecovered;
        ITimer? timer;
        lock (_recoveryLock)
        {
            long since = _awaitingRecoverySince;
            if (since == 0)
            {
                return;
            }

            Volatile.Write(ref _awaitingRecoverySince, 0);
            reportRecovered = recovered && _recoveryOverdueReported;
            waited = TimeProvider.GetElapsedTime(since);
            timer = _recoveryOverdueTimer;
            _recoveryOverdueTimer = null;
        }

        timer?.Dispose();
        if (reportRecovered)
        {
            RecoveredAfterOverdue?.Invoke(this, waited);
        }
    }

    /// <summary>
    /// Decides whether RabbitMQ.Client will recover the channel after this shutdown. The connection rule is a copy
    /// of the one in <c>AutorecoveringConnection</c> (RabbitMQ.Client 7.2.1): nothing is recovered after an
    /// application-initiated close, nor after the peer closed the connection with <c>ACCESS_REFUSED</c> (403);
    /// every other close of a connection that is no longer open is recovered. A channel closed on its own while
    /// the connection stays open is never recovered. Revisit when the client is upgraded.
    /// </summary>
    private bool IsRecoveryExpected(ShutdownEventArgs reason)
    {
        if (!AutomaticRecoveryEnabled || _stopRequested || IsConnectionOpen is null)
        {
            return false;
        }

        if (reason.Initiator == ShutdownInitiator.Application
            || (reason.Initiator == ShutdownInitiator.Peer && reason.ReplyCode == Constants.AccessRefused))
        {
            return false;
        }

        try
        {
            return !IsConnectionOpen();
        }
        catch (ObjectDisposedException)
        {
            // The connection that owned the channel was disposed: it will not be recovered.
            return false;
        }
    }

    /// <summary>
    /// Disposes the buffered deliveries issued by the dead channel. At most the current number of entries is
    /// examined, so an entry that is kept (not stale) and written back to the tail cannot be examined twice.
    /// A kept entry that cannot be written back (the writer is completed) is disposed without settlement; the
    /// broker returns it when the channel closes.
    /// </summary>
    private void DiscardStaleBuffered()
    {
        ChannelReader<InboundMessage> reader = _inboundChannel.Reader;
        int budget = reader.Count;
        while (budget-- > 0 && reader.TryRead(out InboundMessage? message))
        {
            if (!IsStale(message.DeliveryTag) && _inboundChannel.Writer.TryWrite(message))
            {
                continue;
            }

            DiscardStale(message);
        }
    }

    private void DiscardStale(InboundMessage message)
    {
        message.Dispose();
        Interlocked.Increment(ref _discardedStale);
    }
}
