using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Amazon.SQS;
using Amazon.SQS.Model;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS.Internal;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AWS.SQS;

/// <summary>
/// Consumer side of the Amazon SQS transport adapter.
/// Implements <see cref="ITransportAdapter.ConsumeAsync"/> and
/// <see cref="ITransportAdapter.SettleAsync"/>.
/// </summary>
internal sealed partial class SqsTransportAdapter
{
    // Shared in-flight registry — one instance per adapter, shared by every concurrent ConsumeAsync call
    // (one per receive endpoint). Created eagerly in the constructor: lazy initialization would race when
    // several endpoint runners start in parallel and entries would land in an abandoned registry.
    // Bounded by MaxInFlightMessages.
    private readonly SqsInFlightRegistry _inFlightRegistry;

    /// <summary>
    /// Gets the single in-flight registry shared by all consumers of this adapter. Exposed for tests.
    /// </summary>
    internal SqsInFlightRegistry InFlightRegistry => _inFlightRegistry;

    /// <summary>
    /// Gets or sets the total time a stopping consumer may spend on broker calls while handing back
    /// the messages it still holds. Defaults to five seconds; settable for tests only.
    /// </summary>
    internal TimeSpan ShutdownDrainBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fixed pause of a polling loop whose endpoint buffer or the shared registry has no free slot. Slots are
    /// freed by settlement, which is fast compared with long polling, so the pause does not back off.
    /// </summary>
    internal static readonly TimeSpan RegistryFullBackoff = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Runs a long-polling loop via <c>IAmazonSQS.ReceiveMessageAsync</c> with
    /// <c>WaitTimeSeconds</c> from <see cref="SqsTransportOptions"/> and
    /// <c>MaxNumberOfMessages = min(10, flowControl.InternalQueueCapacity)</c>.
    /// Received messages are pushed into a bounded <see cref="Channel{T}"/> (the pipeline is back-pressured).
    /// </para>
    /// <para>
    /// Before every receive the loop atomically reserves slots in the shared <see cref="SqsInFlightRegistry"/>
    /// (at most the free room of the endpoint buffer, <c>flowControl.MaxInFlightMessages</c>) and requests only
    /// that many messages; each received message converts one reservation into a registry entry before it is
    /// buffered. Unused reservations are returned when the iteration ends, and messages that cannot be
    /// registered are handed back to the broker in one batch call. On every release path (cancellation,
    /// channel closed) the registry entry is evicted to prevent unbounded growth.
    /// </para>
    /// <para>
    /// The internal buffer holds at least <c>flowControl.MaxInFlightMessages</c> messages, even when
    /// <c>flowControl.InternalQueueCapacity</c> is smaller, so a received and registered message always fits.
    /// Only <see cref="BoundedChannelFullMode.Wait"/> is supported: a <c>Drop*</c> mode would discard
    /// a received message without returning it to the broker.
    /// </para>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <c>flowControl.MaxInFlightMessages</c> or <c>flowControl.InternalQueueCapacity</c> is not positive,
    /// or <c>flowControl.FullMode</c> is not <see cref="BoundedChannelFullMode.Wait"/>.
    /// </exception>
    /// </remarks>
    public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);
        ValidateFlowControl(flowControl);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        string queueUrl = await GetOrResolveQueueUrlAsync(endpointName, cancellationToken)
            .ConfigureAwait(false);

        int bufferCapacity = Math.Max(1, flowControl.MaxInFlightMessages);

        // The endpoint buffer holds at most bufferCapacity messages (the buffered counter gates every receive),
        // so a channel at least that large is never full when the polling loop writes: TryWrite cannot fail.
        var inboundChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(Math.Max(flowControl.InternalQueueCapacity, bufferCapacity))
            {
                FullMode = flowControl.FullMode, // always Wait after validation
                SingleWriter = true,
                SingleReader = false,
            });

        // The polling loop runs on its own token, linked to the consume token, so that disposing the
        // enumerator stops it even when the consume token is never cancelled.
        using var pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var shutdownBudget = new SqsShutdownBudget(ShutdownDrainBudget);

        // Messages buffered for this endpoint and not yet handed to the caller. An interlocked counter is used
        // instead of Channel.Reader.Count, which bounded-channel consumers must not depend on.
        var buffered = new BufferedCounter();

        // Start the polling loop as a background task.
        Task pollingTask = RunPollingLoopAsync(
            queueUrl, endpointName, flowControl, bufferCapacity, inboundChannel.Writer, buffered, shutdownBudget,
            pollingCts.Token);

        // Yield messages as they arrive; complete when cancellation fires or the loop stops.
        try
        {
            await foreach (InboundMessage message in SqsShutdownDrain
                .ReadUntilCancelledAsync(inboundChannel.Reader, cancellationToken).ConfigureAwait(false))
            {
                buffered.Decrement();
                yield return message;
            }
        }
        finally
        {
            // Shutdown order matters. Messages the caller already holds are settled by the caller through
            // SettleAsync; everything else this consumer received must go back to the broker:
            //   1. Stop the polling loop and wait for it (it hands back what it received after cancellation).
            //   2. Drain the buffer: dispose each message, evict its registry entry and make it visible
            //      again, all within one shared time budget so a stuck broker cannot stall shutdown.
            await pollingCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await pollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on graceful cancellation.
            }

            await SqsShutdownDrain.DrainAndReleaseAsync(
                inboundChannel.Reader, _inFlightRegistry, _client!, queueUrl, endpointName,
                shutdownBudget, _logger).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Evicts the registry entry exactly once (evict-once semantics), maps the
    /// <paramref name="action"/> via <see cref="SqsSettlementRouter"/>, and calls the
    /// corresponding SQS API:
    /// <list type="bullet">
    /// <item><term>Ack</term><description>→ <c>DeleteMessageAsync</c></description></item>
    /// <item><term>Nack / Requeue / Defer</term><description>→ <c>ChangeMessageVisibilityAsync(VisibilityTimeout=0)</c></description></item>
    /// <item>
    /// <term>Reject</term>
    /// <description>
    /// → no destructive operation; the message remains in the queue and will be moved to the
    /// DLQ after <c>maxReceiveCount</c> is exhausted via RedrivePolicy.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public async Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Evict-once: returns null on miss or when already evicted.
        (string ReceiptHandle, string QueueUrl)? entry =
            _inFlightRegistry.TryEvict(message.DeliveryTag);

        if (entry is null)
        {
            throw new BareWireTransportException(
                message: $"Cannot settle message: no registry entry found for " +
                         $"DeliveryTag={message.DeliveryTag}. " +
                         "The message may already have been settled or the consumer stopped.",
                transportName: TransportName,
                endpointAddress: null);
        }

        SqsSettlementOperation operation = SqsSettlementRouter.Map(action);

        try
        {
            switch (operation)
            {
                case SqsSettlementOperation.Delete:
                    await _client!.DeleteMessageAsync(
                        new DeleteMessageRequest(entry.Value.QueueUrl, entry.Value.ReceiptHandle),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case SqsSettlementOperation.ChangeVisibility:
                    // Visibility = 0 makes the message immediately visible for redelivery.
                    await _client!.ChangeMessageVisibilityAsync(
                        new ChangeMessageVisibilityRequest(
                            entry.Value.QueueUrl,
                            entry.Value.ReceiptHandle,
                            visibilityTimeout: 0),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case SqsSettlementOperation.DeadLetterViaRedrive:
                    // Do NOT delete or change visibility for Reject.
                    // Leave the message in the queue — its receive count will be incremented
                    // on the next ReceiveMessage call, and the RedrivePolicy will move it to
                    // the DLQ once maxReceiveCount is exhausted. Deleting here would discard
                    // the message without triggering the DLQ, causing silent data loss.
                    LogRejectViaRedrive(message.DeliveryTag);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown SqsSettlementOperation value: {operation}.");
            }
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException &&
            ex is not BareWireTransportException &&
            ex is not InvalidOperationException)
        {
            throw new BareWireTransportException(
                message: $"Failed to settle SQS message (action={action}, " +
                         $"DeliveryTag={message.DeliveryTag}).",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }
    }

    private static void ValidateFlowControl(FlowControlOptions flowControl)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flowControl.MaxInFlightMessages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flowControl.InternalQueueCapacity);

        if (flowControl.FullMode != BoundedChannelFullMode.Wait)
        {
            // A Drop* mode would discard a received, registered message without returning it to the broker.
            throw new ArgumentOutOfRangeException(
                nameof(flowControl),
                flowControl.FullMode,
                "Amazon SQS consumers support only BoundedChannelFullMode.Wait.");
        }
    }

    // ── Polling loop ─────────────────────────────────────────────────────────

    private async Task RunPollingLoopAsync(
        string queueUrl,
        string endpointName,
        FlowControlOptions flowControl,
        int bufferCapacity,
        ChannelWriter<InboundMessage> writer,
        BufferedCounter buffered,
        SqsShutdownBudget shutdownBudget,
        CancellationToken cancellationToken)
    {
        int maxMessages = Math.Min(_options.MaxNumberOfMessages, flowControl.InternalQueueCapacity);
        maxMessages = Math.Max(1, maxMessages); // must be at least 1

        LogConsumerStarted(endpointName);

        bool paused = false;

        _inFlightRegistry.EnterConsumer();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Reserve slots atomically BEFORE calling the broker: concurrent endpoints can never hold more
                // than MaxInFlightMessages in total, and no message is received that cannot be registered.
                // The request is capped to this endpoint's fair share of the registry so one endpoint's
                // long poll can never pin the whole shared limit while another is starved.
                int requested = _inFlightRegistry.GetFairShare(
                    Math.Min(maxMessages, bufferCapacity - buffered.Value));
                int granted = requested > 0 ? _inFlightRegistry.TryReserve(requested) : 0;

                if (granted == 0)
                {
                    if (!paused)
                    {
                        paused = true;
                        LogReceivePaused(endpointName);
                    }

                    await Task.Delay(RegistryFullBackoff, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                paused = false;
                int used = 0;

                try
                {
                    ReceiveMessageResponse response;
                    try
                    {
                        response = await _client!.ReceiveMessageAsync(
                            new ReceiveMessageRequest
                            {
                                QueueUrl = queueUrl,
                                WaitTimeSeconds = _options.WaitTimeSeconds,
                                MaxNumberOfMessages = granted,
                                MessageAttributeNames = ["All"],
                                MessageSystemAttributeNames = ["MessageGroupId", "SequenceNumber", "MessageDeduplicationId"],
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        SqsErrorInfo error = SqsErrorInfo.From(ex);
                        LogPollingError(endpointName, error.ExceptionType, error.ErrorCode, error.StatusCode);
                        // Brief pause before retry to avoid tight error loops.
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    // AWSSDK.SQS v4 leaves Messages null (not empty) when the long poll times out.
                    List<Message>? receivedMessages = response.Messages;
                    if (receivedMessages is null || receivedMessages.Count == 0)
                    {
                        continue;
                    }

                    for (int messageIndex = 0; messageIndex < receivedMessages.Count; messageIndex++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            // Received but never registered or buffered: give the rest of the batch back now
                            // instead of leaving it invisible until the visibility timeout expires.
                            await SqsShutdownDrain.ReleaseUnprocessedAsync(
                                _client!, queueUrl, endpointName, receivedMessages, messageIndex,
                                shutdownBudget, _logger).ConfigureAwait(false);
                            break;
                        }

                        Message sqsMessage = receivedMessages[messageIndex];

                        ulong deliveryTag =
                            System.Threading.Interlocked.Increment(ref _deliveryTagCounter);

                        // Convert one reservation into a registry entry BEFORE building the InboundMessage, so
                        // a message that cannot be registered never allocates a body buffer. The broker may
                        // return more than requested (index >= granted) or the tag may collide.
                        if (messageIndex >= granted
                            || !_inFlightRegistry.RegisterReserved(deliveryTag, sqsMessage.ReceiptHandle, queueUrl))
                        {
                            // The rest of the batch goes back in ONE batch call (keeps FIFO group order) with
                            // its own budget: the shutdown budget's clock must not start during normal work.
                            LogRegistryFull(receivedMessages.Count - messageIndex, endpointName);
                            using var releaseBudget = new SqsShutdownBudget(ShutdownDrainBudget);
                            await SqsShutdownDrain.ReleaseUnprocessedAsync(
                                _client!, queueUrl, endpointName, receivedMessages, messageIndex,
                                releaseBudget, _logger).ConfigureAwait(false);
                            break;
                        }

                        used++;

                        InboundMessage inbound;
                        try
                        {
                            inbound = BuildInboundMessage(sqsMessage, deliveryTag);
                        }
                        catch
                        {
                            // Never leave a registered entry behind that no consumer can ever settle.
                            _inFlightRegistry.TryEvict(deliveryTag);
                            throw;
                        }

                        if (!writer.TryWrite(inbound))
                        {
                            // Defensive: unreachable while the channel is at least as large as the endpoint
                            // buffer (or when it has been closed). Never drop silently: this message and the
                            // rest of the batch go back to the broker now.
                            _inFlightRegistry.TryEvict(deliveryTag);
                            inbound.Dispose();
                            LogMessageDropped(deliveryTag, endpointName);

                            // Read the token state once, before choosing the budget, so the decision cannot
                            // flip between the check and the call.
                            bool stopping = cancellationToken.IsCancellationRequested;

                            if (stopping)
                            {
                                // Stopping: share the one shutdown budget with the rest of the drain.
                                await SqsShutdownDrain.ReleaseUnprocessedAsync(
                                    _client!, queueUrl, endpointName, receivedMessages, messageIndex,
                                    shutdownBudget, _logger).ConfigureAwait(false);
                            }
                            else
                            {
                                // Normal operation: a fresh budget, so the shutdown clock does not start
                                // while the consumer is running.
                                using var releaseBudget = new SqsShutdownBudget(ShutdownDrainBudget);
                                await SqsShutdownDrain.ReleaseUnprocessedAsync(
                                    _client!, queueUrl, endpointName, receivedMessages, messageIndex,
                                    releaseBudget, _logger).ConfigureAwait(false);
                            }

                            break;
                        }

                        buffered.Increment();
                    }
                }
                finally
                {
                    // Reservations that never became entries (empty poll, error, cancellation, refusal).
                    _inFlightRegistry.ReleaseReservations(granted - used);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            SqsErrorInfo error = SqsErrorInfo.From(ex);
            LogPollingFatalError(endpointName, error.ExceptionType, error.ErrorCode, error.StatusCode);
        }
        finally
        {
            _inFlightRegistry.ExitConsumer();
            writer.TryComplete();
            LogConsumerStopped(endpointName);
        }
    }

    private static InboundMessage BuildInboundMessage(Message sqsMessage, ulong deliveryTag)
    {
        Dictionary<string, string> headers = SqsHeaderMapper.MapInbound(
            sqsMessage.MessageAttributes);

        // Stamp trusted FIFO system attributes AFTER MapInbound (anti-squatting).
        // System attributes are set by the SQS broker (not the sender) and cannot be
        // forged via MessageAttributes. Stamping after MapInbound ensures broker values
        // always win over any sender-supplied MessageAttribute with the same BW key.
        if (sqsMessage.Attributes is not null)
        {
            if (sqsMessage.Attributes.TryGetValue("MessageGroupId", out string? gid) &&
                !string.IsNullOrEmpty(gid))
            {
                headers[SqsHeaderMapper.MessageGroupIdHeader] = gid;
            }

            if (sqsMessage.Attributes.TryGetValue("SequenceNumber", out string? seq) &&
                !string.IsNullOrEmpty(seq))
            {
                headers[SqsHeaderMapper.SequenceNumberHeader] = seq;
            }
        }

        // Determine content type from headers for body decoding.
        string contentType = headers.TryGetValue("content-type", out string? ct)
            ? ct
            : "application/json";

        // Decode the body back to bytes (Base64 for binary, UTF-8 for text).
        ReadOnlyMemory<byte> bodyMemory =
            SqsHeaderMapper.DecodeBodyBytes(sqsMessage.Body, contentType);

        // Copy to a pooled buffer to give InboundMessage proper ownership/lifetime.
        byte[]? pooledBuffer = null;
        ReadOnlySequence<byte> bodySequence;

        if (bodyMemory.Length > 0)
        {
            pooledBuffer = ArrayPool<byte>.Shared.Rent(bodyMemory.Length);
            bodyMemory.CopyTo(pooledBuffer);
            bodySequence = new ReadOnlySequence<byte>(
                pooledBuffer, 0, bodyMemory.Length);
        }
        else
        {
            bodySequence = ReadOnlySequence<byte>.Empty;
        }

        return new InboundMessage(
            messageId: sqsMessage.MessageId,
            headers: headers,
            body: bodySequence,
            deliveryTag: deliveryTag,
            pooledBuffer: pooledBuffer);
    }

    /// <summary>Interlocked count of messages buffered for one endpoint and not yet handed to the caller.</summary>
    private sealed class BufferedCounter
    {
        private int _value;

        internal int Value => Volatile.Read(ref _value);

        internal void Increment() => Interlocked.Increment(ref _value);

        internal void Decrement() => Interlocked.Decrement(ref _value);
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQS consumer started for queue '{QueueName}'.")]
    private partial void LogConsumerStarted(string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQS consumer stopped for queue '{QueueName}'.")]
    private partial void LogConsumerStopped(string queueName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS polling error for queue '{QueueName}': {ExceptionType}, ErrorCode={ErrorCode}, " +
                  "StatusCode={StatusCode}. Retrying.")]
    private partial void LogPollingError(string queueName, string exceptionType, string errorCode, int statusCode);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "SQS polling fatal error for queue '{QueueName}': {ExceptionType}, ErrorCode={ErrorCode}, " +
                  "StatusCode={StatusCode}. Consumer loop exiting.")]
    private partial void LogPollingFatalError(string queueName, string exceptionType, string errorCode, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS in-flight registry rejected a received message for queue '{QueueName}' — returning " +
                  "{Count} message(s) to the broker so they become visible again.")]
    private partial void LogRegistryFull(int count, string queueName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "In-flight registry or endpoint buffer full for endpoint '{EndpointName}'; pausing receive.")]
    private partial void LogReceivePaused(string endpointName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS message DeliveryTag={DeliveryTag} could not be buffered for queue '{QueueName}'; returning it to the broker.")]
    private partial void LogMessageDropped(ulong deliveryTag, string queueName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SQS Reject (DeliveryTag={DeliveryTag}): leaving message for RedrivePolicy DLQ.")]
    private partial void LogRejectViaRedrive(ulong deliveryTag);
}
