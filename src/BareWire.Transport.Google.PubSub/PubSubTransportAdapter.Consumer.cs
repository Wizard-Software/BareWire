using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Google.PubSub.Internal;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Google.PubSub;

/// <summary>
/// Consumer side of the Google Cloud Pub/Sub transport adapter.
/// Implements <see cref="ITransportAdapter.ConsumeAsync"/> and
/// <see cref="ITransportAdapter.SettleAsync"/>.
/// </summary>
internal sealed partial class PubSubTransportAdapter
{
    // Shared in-flight registry — one instance per adapter (one Pub/Sub project endpoint).
    // Bounded by MaxInFlightMessages (PERF-3 mitigation).
    private PubSubInFlightRegistry? _inFlightRegistry;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Runs a long-polling loop via <c>SubscriberServiceApiClient.PullAsync</c> with
    /// <c>maxMessages = min(MaxOutstandingMessages, flowControl.InternalQueueCapacity)</c>.
    /// Received messages are pushed into a bounded <see cref="Channel{T}"/> (ADR-004).
    /// </para>
    /// <para>
    /// Each message is registered in the <see cref="PubSubInFlightRegistry"/> immediately after
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

        await EnsureClientsAsync(cancellationToken).ConfigureAwait(false);

        // Lazily create the registry (one per adapter instance).
        _inFlightRegistry ??= new PubSubInFlightRegistry(_options.MaxInFlightMessages);

        var subscriptionName = SubscriptionName.FromProjectSubscription(_options.ProjectId, endpointName);

        var inboundChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                FullMode = flowControl.FullMode,
                SingleWriter = true,
                SingleReader = false,
            });

        // Start the polling loop as a background task.
        Task pollingTask = RunPollingLoopAsync(
            subscriptionName, flowControl, inboundChannel.Writer, cancellationToken);

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
    /// <paramref name="action"/> via <see cref="PubSubSettlementRouter"/>, and calls the
    /// corresponding Pub/Sub API:
    /// <list type="bullet">
    /// <item><term>Ack</term><description>→ <c>AcknowledgeAsync(subscriptionName, [ackId])</c></description></item>
    /// <item><term>Nack / Requeue / Defer</term><description>→ <c>ModifyAckDeadlineAsync(subscriptionName, [ackId], ackDeadlineSeconds: 0)</c></description></item>
    /// <item>
    /// <term>Reject</term>
    /// <description>
    /// → no broker operation (DeadLetterViaPolicy); the message remains for redelivery until
    /// <c>max_delivery_attempts</c> is exhausted and <c>DeadLetterPolicy</c> routes it to the
    /// dead-letter topic (full wiring R5.3 / ADR-017).
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
        (string AckId, string SubscriptionName)? entry =
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

        PubSubSettlementOperation operation = PubSubSettlementRouter.Map(action);

        try
        {
            switch (operation)
            {
                case PubSubSettlementOperation.Acknowledge:
                    await _subscriber!.AcknowledgeAsync(
                        entry.Value.SubscriptionName,
                        [entry.Value.AckId],
                        cancellationToken).ConfigureAwait(false);
                    break;

                case PubSubSettlementOperation.ModifyAckDeadlineZero:
                    // ackDeadlineSeconds = 0 makes the message immediately visible for redelivery
                    // (Pub/Sub nack idiom — analogous to SQS ChangeVisibility(0)).
                    await _subscriber!.ModifyAckDeadlineAsync(
                        entry.Value.SubscriptionName,
                        [entry.Value.AckId],
                        ackDeadlineSeconds: 0,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case PubSubSettlementOperation.DeadLetterViaPolicy:
                    // ADR-017 / R5.3: do NOT ack or modify deadline for Reject.
                    // Leave the message for redelivery — DeadLetterPolicy routes it to the
                    // dead-letter topic once max_delivery_attempts is exhausted.
                    LogRejectViaDeadLetterPolicy(message.DeliveryTag);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown PubSubSettlementOperation value: {operation}.");
            }
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException &&
            ex is not BareWireTransportException &&
            ex is not InvalidOperationException)
        {
            throw new BareWireTransportException(
                message: $"Failed to settle Pub/Sub message (action={action}, " +
                         $"DeliveryTag={message.DeliveryTag}).",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }
    }

    // ── Polling loop ─────────────────────────────────────────────────────────

    private async Task RunPollingLoopAsync(
        SubscriptionName subscriptionName,
        FlowControlOptions flowControl,
        ChannelWriter<InboundMessage> writer,
        CancellationToken cancellationToken)
    {
        int maxMessages = Math.Min(_options.MaxOutstandingMessages, flowControl.InternalQueueCapacity);
        maxMessages = Math.Max(1, maxMessages); // must be at least 1

        string subscriptionNameStr = subscriptionName.ToString();

        LogConsumerStarted(subscriptionNameStr);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PullResponse response;
                try
                {
                    response = await _subscriber!.PullAsync(
                        subscriptionName,
                        maxMessages,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogPollingError(subscriptionNameStr, ex.Message);
                    // Brief pause before retry to avoid tight error loops.
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                // Back-off on empty response to avoid busy-spin and unnecessary RPC cost.
                if (response.ReceivedMessages.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                foreach (ReceivedMessage receivedMessage in response.ReceivedMessages)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    ulong deliveryTag =
                        System.Threading.Interlocked.Increment(ref _deliveryTagCounter);

                    // Check registry capacity before allocating the InboundMessage body buffer (PERF-3).
                    if (_inFlightRegistry!.Count >= _options.MaxInFlightMessages)
                    {
                        LogRegistryFull(deliveryTag, subscriptionNameStr);
                        continue;
                    }

                    PubsubMessage pubsubMessage = receivedMessage.Message;

                    // MapInbound: Pub/Sub Attributes → BareWire headers.
                    Dictionary<string, string> headers = PubSubHeaderMapper.MapInbound(
                        pubsubMessage.Attributes);

                    // Propagate ordering key into BareWire headers if present (R5.1 pass-through).
                    if (!string.IsNullOrEmpty(pubsubMessage.OrderingKey))
                    {
                        headers[PubSubHeaderMapper.OrderingKeyHeader] = pubsubMessage.OrderingKey;
                    }

                    // Copy body bytes to a pooled buffer to give InboundMessage proper ownership (ADR-003).
                    ReadOnlyMemory<byte> bodyMemory = pubsubMessage.Data.Memory;
                    byte[]? pooledBuffer = null;
                    ReadOnlySequence<byte> bodySequence;

                    if (bodyMemory.Length > 0)
                    {
                        pooledBuffer = ArrayPool<byte>.Shared.Rent(bodyMemory.Length);
                        bodyMemory.CopyTo(pooledBuffer);
                        bodySequence = new ReadOnlySequence<byte>(pooledBuffer, 0, bodyMemory.Length);
                    }
                    else
                    {
                        bodySequence = ReadOnlySequence<byte>.Empty;
                    }

                    var inbound = new InboundMessage(
                        messageId: pubsubMessage.MessageId,
                        headers: headers,
                        body: bodySequence,
                        deliveryTag: deliveryTag,
                        pooledBuffer: pooledBuffer);

                    // Register BEFORE writing to channel (evict on any drop path — PERF-3).
                    bool registered = _inFlightRegistry!.TryRegister(
                        deliveryTag, receivedMessage.AckId, subscriptionNameStr);

                    if (!registered)
                    {
                        // Registry rejected (race — count reached max between our check and TryAdd).
                        inbound.Dispose();
                        LogRegistryFull(deliveryTag, subscriptionNameStr);
                        continue;
                    }

                    // Attempt to write to the bounded channel.
                    bool written = writer.TryWrite(inbound);

                    if (!written)
                    {
                        // Channel is full under Drop* FullMode.
                        // PERF-3: evict from registry on every drop path.
                        _inFlightRegistry!.TryEvict(deliveryTag);
                        inbound.Dispose();
                        LogMessageDropped(deliveryTag, subscriptionNameStr);
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
            LogPollingFatalError(subscriptionNameStr, ex.Message);
        }
        finally
        {
            writer.TryComplete();
            LogConsumerStopped(subscriptionNameStr);
        }
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub consumer started for subscription '{SubscriptionName}'.")]
    private partial void LogConsumerStarted(string subscriptionName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub consumer stopped for subscription '{SubscriptionName}'.")]
    private partial void LogConsumerStopped(string subscriptionName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub polling error for subscription '{SubscriptionName}': {ErrorMessage}. Retrying.")]
    private partial void LogPollingError(string subscriptionName, string errorMessage);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Pub/Sub polling fatal error for subscription '{SubscriptionName}': {ErrorMessage}. Consumer loop exiting.")]
    private partial void LogPollingFatalError(string subscriptionName, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub in-flight registry full — dropping message DeliveryTag={DeliveryTag} for subscription '{SubscriptionName}'.")]
    private partial void LogRegistryFull(ulong deliveryTag, string subscriptionName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub channel full (Drop mode) — dropping message DeliveryTag={DeliveryTag} for subscription '{SubscriptionName}'.")]
    private partial void LogMessageDropped(ulong deliveryTag, string subscriptionName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Pub/Sub Reject (DeliveryTag={DeliveryTag}): leaving message for DeadLetterPolicy DLQ (ADR-017).")]
    private partial void LogRejectViaDeadLetterPolicy(ulong deliveryTag);
}
