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
    // Shared in-flight registry — one instance per adapter (one SQS endpoint).
    // Bounded by MaxInFlightMessages (PERF-3 mitigation).
    private SqsInFlightRegistry? _inFlightRegistry;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Runs a long-polling loop via <c>IAmazonSQS.ReceiveMessageAsync</c> with
    /// <c>WaitTimeSeconds</c> from <see cref="SqsTransportOptions"/> and
    /// <c>MaxNumberOfMessages = min(10, flowControl.InternalQueueCapacity)</c>.
    /// Received messages are pushed into a bounded <see cref="Channel{T}"/> (ADR-004).
    /// </para>
    /// <para>
    /// Each message is registered in the <see cref="SqsInFlightRegistry"/> immediately after
    /// a successful <c>TryWrite</c>. On every drop path (registry full, <c>TryWrite</c> failure
    /// under Drop* FullMode, cancellation, channel closed) the registry entry is evicted to
    /// prevent unbounded growth (PERF-3).
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        // Lazily create the registry (one per adapter instance).
        _inFlightRegistry ??= new SqsInFlightRegistry(_options.MaxInFlightMessages);

        string queueUrl = await GetOrResolveQueueUrlAsync(endpointName, cancellationToken)
            .ConfigureAwait(false);

        var inboundChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                FullMode = flowControl.FullMode,
                SingleWriter = true,
                SingleReader = false,
            });

        // Start the polling loop as a background task.
        Task pollingTask = RunPollingLoopAsync(
            queueUrl, endpointName, flowControl, inboundChannel.Writer, cancellationToken);

        // Yield messages as they arrive; complete when cancellation fires or the loop stops.
        try
        {
            await foreach (InboundMessage message in inboundChannel.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            // Signal the polling loop to stop (if not already cancelled).
            inboundChannel.Writer.TryComplete();
            try
            {
                await pollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on graceful cancellation.
            }
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
    /// DLQ after <c>maxReceiveCount</c> is exhausted via RedrivePolicy (ADR-014 / GAP-3).
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

        if (_inFlightRegistry is null)
        {
            throw new BareWireTransportException(
                message: "Cannot settle message: no consumer has been started on this adapter.",
                transportName: TransportName,
                endpointAddress: null);
        }

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
                    // GAP-3 / ADR-014: do NOT delete or change visibility for Reject.
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

    // ── Polling loop ─────────────────────────────────────────────────────────

    private async Task RunPollingLoopAsync(
        string queueUrl,
        string endpointName,
        FlowControlOptions flowControl,
        ChannelWriter<InboundMessage> writer,
        CancellationToken cancellationToken)
    {
        int maxMessages = Math.Min(_options.MaxNumberOfMessages, flowControl.InternalQueueCapacity);
        maxMessages = Math.Max(1, maxMessages); // must be at least 1

        LogConsumerStarted(endpointName);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReceiveMessageResponse response;
                try
                {
                    response = await _client!.ReceiveMessageAsync(
                        new ReceiveMessageRequest
                        {
                            QueueUrl = queueUrl,
                            WaitTimeSeconds = _options.WaitTimeSeconds,
                            MaxNumberOfMessages = maxMessages,
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
                    LogPollingError(endpointName, ex.Message);
                    // Brief pause before retry to avoid tight error loops.
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                foreach (Message sqsMessage in response.Messages)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    ulong deliveryTag =
                        System.Threading.Interlocked.Increment(ref _deliveryTagCounter);

                    // Check registry capacity before allocating the InboundMessage body buffer.
                    if (_inFlightRegistry!.Count >= _options.MaxInFlightMessages)
                    {
                        // PERF-3: registry at capacity — skip this message.
                        // It will become visible again after its visibility timeout.
                        LogRegistryFull(deliveryTag, endpointName);
                        continue;
                    }

                    Dictionary<string, string> headers = SqsHeaderMapper.MapInbound(
                        sqsMessage.MessageAttributes);

                    // Stamp trusted FIFO system attributes AFTER MapInbound (SEC-3 anti-squatting).
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

                    var inbound = new InboundMessage(
                        messageId: sqsMessage.MessageId,
                        headers: headers,
                        body: bodySequence,
                        deliveryTag: deliveryTag,
                        pooledBuffer: pooledBuffer);

                    // Register BEFORE writing to channel (evict on any drop path — PERF-3).
                    bool registered = _inFlightRegistry!.TryRegister(
                        deliveryTag, sqsMessage.ReceiptHandle, queueUrl);

                    if (!registered)
                    {
                        // Registry rejected (race — count reached max between our check and TryAdd).
                        // PERF-3: evict path — return pooled buffer.
                        inbound.Dispose();
                        LogRegistryFull(deliveryTag, endpointName);
                        continue;
                    }

                    // Attempt to write to the bounded channel.
                    bool written = writer.TryWrite(inbound);

                    if (!written)
                    {
                        // Channel is full under Drop* FullMode (Wait mode would block, not return false).
                        // PERF-3: evict from registry on every drop path.
                        _inFlightRegistry!.TryEvict(deliveryTag);
                        inbound.Dispose();
                        LogMessageDropped(deliveryTag, endpointName);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LogPollingFatalError(endpointName, ex.Message);
        }
        finally
        {
            writer.TryComplete();
            LogConsumerStopped(endpointName);
        }
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQS consumer started for queue '{QueueName}'.")]
    private partial void LogConsumerStarted(string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQS consumer stopped for queue '{QueueName}'.")]
    private partial void LogConsumerStopped(string queueName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS polling error for queue '{QueueName}': {ErrorMessage}. Retrying.")]
    private partial void LogPollingError(string queueName, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "SQS polling fatal error for queue '{QueueName}': {ErrorMessage}. Consumer loop exiting.")]
    private partial void LogPollingFatalError(string queueName, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS in-flight registry full — dropping message DeliveryTag={DeliveryTag} for queue '{QueueName}'.")]
    private partial void LogRegistryFull(ulong deliveryTag, string queueName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS channel full (Drop mode) — dropping message DeliveryTag={DeliveryTag} for queue '{QueueName}'.")]
    private partial void LogMessageDropped(ulong deliveryTag, string queueName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SQS Reject (DeliveryTag={DeliveryTag}): leaving message for RedrivePolicy DLQ (ADR-014).")]
    private partial void LogRejectViaRedrive(ulong deliveryTag);
}
