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
    // Shared in-flight registry — one instance per adapter, shared by every concurrent ConsumeAsync call
    // (one per receive endpoint). Created eagerly in the constructor: lazy initialization would race when
    // several endpoint runners start in parallel and entries would land in an abandoned registry.
    // Bounded by MaxInFlightMessages (PERF-3 mitigation).
    private readonly PubSubInFlightRegistry _inFlightRegistry;

    /// <summary>
    /// Gets the single in-flight registry shared by all consumers of this adapter. Exposed for tests.
    /// </summary>
    internal PubSubInFlightRegistry InFlightRegistry => _inFlightRegistry;

    /// <summary>
    /// Gets or sets the total time a stopping consumer may spend on broker calls while handing back
    /// the messages it still holds. Defaults to five seconds; settable for tests only.
    /// </summary>
    internal TimeSpan ShutdownDrainBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fixed pause of a pull loop whose endpoint buffer or the shared registry has no free slot. Slots are
    /// freed by settlement, which is fast compared with a pull round trip, so the pause does not back off.
    /// </summary>
    internal static readonly TimeSpan RegistryFullBackoff = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Runs a long-polling loop via <c>SubscriberServiceApiClient.PullAsync</c> with
    /// <c>maxMessages = min(MaxOutstandingMessages, flowControl.InternalQueueCapacity)</c>.
    /// Received messages are pushed into a bounded <see cref="Channel{T}"/> (ADR-004).
    /// </para>
    /// <para>
    /// Before every pull the loop atomically reserves slots in the shared <see cref="PubSubInFlightRegistry"/>
    /// (at most the free room of the endpoint buffer, <c>flowControl.MaxInFlightMessages</c>) and requests only
    /// that many messages; each received message converts one reservation into a registry entry before it is
    /// buffered. Unused reservations are returned when the iteration ends, and messages that cannot be
    /// registered are handed back to the broker in one batch call. On every drop path (<c>TryWrite</c>
    /// failure under Drop* FullMode, cancellation, channel closed) the registry entry is evicted to
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

        var subscriptionName = SubscriptionName.FromProjectSubscription(_options.ProjectId, endpointName);

        var inboundChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                FullMode = flowControl.FullMode,
                SingleWriter = true,
                SingleReader = false,
            });

        // The polling loop runs on its own token, linked to the consume token, so that disposing the
        // enumerator stops it even when the consume token is never cancelled.
        using var pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var shutdownBudget = new PubSubShutdownBudget(ShutdownDrainBudget);

        string subscriptionNameStr = subscriptionName.ToString();

        // Messages buffered for this endpoint and not yet handed to the caller. An interlocked counter is used
        // instead of Channel.Reader.Count, which bounded-channel consumers must not depend on.
        var buffered = new BufferedCounter();

        // Start the polling loop as a background task.
        Task pollingTask = RunPollingLoopAsync(
            subscriptionName, flowControl, inboundChannel.Writer, buffered, shutdownBudget, pollingCts.Token);

        // Yield messages as they arrive; complete when cancellation fires or the loop stops.
        try
        {
            await foreach (InboundMessage message in PubSubShutdownDrain
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
            //   2. Drain the buffer: dispose each message, evict its registry entry and make it deliverable
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

            await PubSubShutdownDrain.DrainAndReleaseAsync(
                inboundChannel.Reader, _inFlightRegistry, _subscriber!, subscriptionNameStr,
                shutdownBudget, _logger).ConfigureAwait(false);
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
        BufferedCounter buffered,
        PubSubShutdownBudget shutdownBudget,
        CancellationToken cancellationToken)
    {
        int maxMessages = Math.Min(_options.MaxOutstandingMessages, flowControl.InternalQueueCapacity);
        maxMessages = Math.Max(1, maxMessages); // must be at least 1

        string subscriptionNameStr = subscriptionName.ToString();

        LogConsumerStarted(subscriptionNameStr);

        // The endpoint buffer never holds more than the prefetch window (flowControl.MaxInFlightMessages).
        int bufferCapacity = Math.Max(1, flowControl.MaxInFlightMessages);
        bool paused = false;

        _inFlightRegistry.EnterConsumer();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Reserve slots atomically BEFORE calling the broker: concurrent endpoints can never hold more
                // than MaxInFlightMessages in total, and no message is pulled that cannot be registered.
                // The request is capped to this endpoint's fair share of the registry so an idle subscription
                // can never pin the whole shared limit while a busy one is starved.
                int requested = _inFlightRegistry.GetFairShare(
                    Math.Min(maxMessages, bufferCapacity - buffered.Value));
                int granted = requested > 0 ? _inFlightRegistry.TryReserve(requested) : 0;

                if (granted == 0)
                {
                    if (!paused)
                    {
                        paused = true;
                        LogReceivePaused(subscriptionNameStr);
                    }

                    await Task.Delay(RegistryFullBackoff, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                paused = false;
                int used = 0;
                TimeSpan backoff = TimeSpan.Zero;

                try
                {
                    PullResponse? response = null;
                    try
                    {
                        response = await _subscriber!.PullAsync(
                            subscriptionName,
                            granted,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        PubSubErrorInfo error = PubSubErrorInfo.From(ex);
                        LogPollingError(subscriptionNameStr, error.ExceptionType, error.StatusCode);
                        // Brief pause before retry to avoid tight error loops; served AFTER the reservation
                        // is released (below) so the pause never pins registry slots.
                        backoff = TimeSpan.FromSeconds(1);
                    }

                    if (response is not null && response.ReceivedMessages.Count == 0)
                    {
                        // Back-off on empty response to avoid busy-spin and unnecessary RPC cost.
                        backoff = TimeSpan.FromMilliseconds(100);
                    }
                    else if (response is not null)
                    {
                        for (int messageIndex = 0; messageIndex < response.ReceivedMessages.Count; messageIndex++)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                // Received but never registered or buffered: give the rest of the batch back now
                                // instead of leaving it leased until the ack deadline expires.
                                await PubSubShutdownDrain.ReleaseUnprocessedAsync(
                                    _subscriber!, subscriptionNameStr, response.ReceivedMessages, messageIndex,
                                    shutdownBudget, _logger).ConfigureAwait(false);
                                break;
                            }

                            ReceivedMessage receivedMessage = response.ReceivedMessages[messageIndex];

                            ulong deliveryTag =
                                System.Threading.Interlocked.Increment(ref _deliveryTagCounter);

                            // Convert one reservation into a registry entry BEFORE building the InboundMessage, so
                            // a message that cannot be registered never allocates a body buffer. The broker may
                            // return more than requested (index >= granted) or the tag may collide.
                            if (messageIndex >= granted
                                || !_inFlightRegistry.RegisterReserved(
                                    deliveryTag, receivedMessage.AckId, subscriptionNameStr))
                            {
                                // The rest of the batch goes back in ONE batch call with its own budget: the
                                // shutdown budget's clock must not start during normal work.
                                LogRegistryFull(response.ReceivedMessages.Count - messageIndex, subscriptionNameStr);
                                using var releaseBudget = new PubSubShutdownBudget(ShutdownDrainBudget);
                                await PubSubShutdownDrain.ReleaseUnprocessedAsync(
                                    _subscriber!, subscriptionNameStr, response.ReceivedMessages, messageIndex,
                                    releaseBudget, _logger).ConfigureAwait(false);
                                break;
                            }

                            used++;

                            InboundMessage inbound;
                            try
                            {
                                inbound = BuildInboundMessage(receivedMessage.Message, deliveryTag);
                            }
                            catch
                            {
                                // Never leave a registered entry behind that no consumer can ever settle.
                                _inFlightRegistry.TryEvict(deliveryTag);
                                throw;
                            }

                            // Attempt to write to the bounded channel.
                            bool written = writer.TryWrite(inbound);

                            if (written)
                            {
                                buffered.Increment();
                            }
                            else
                            {
                                // Channel is full under Drop* FullMode.
                                // PERF-3: evict from registry on every drop path.
                                _inFlightRegistry.TryEvict(deliveryTag);
                                inbound.Dispose();
                                LogMessageDropped(deliveryTag, subscriptionNameStr);
                            }
                        }
                    }
                }
                finally
                {
                    // Reservations that never became entries (empty pull, error, cancellation, refusal).
                    _inFlightRegistry.ReleaseReservations(granted - used);
                }

                if (backoff > TimeSpan.Zero)
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            PubSubErrorInfo error = PubSubErrorInfo.From(ex);
            LogPollingFatalError(subscriptionNameStr, error.ExceptionType, error.StatusCode);
        }
        finally
        {
            _inFlightRegistry.ExitConsumer();
            writer.TryComplete();
            LogConsumerStopped(subscriptionNameStr);
        }
    }

    private static InboundMessage BuildInboundMessage(PubsubMessage pubsubMessage, ulong deliveryTag)
    {
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

        return new InboundMessage(
            messageId: pubsubMessage.MessageId,
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
        Message = "Pub/Sub consumer started for subscription '{SubscriptionName}'.")]
    private partial void LogConsumerStarted(string subscriptionName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub consumer stopped for subscription '{SubscriptionName}'.")]
    private partial void LogConsumerStopped(string subscriptionName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub polling error for subscription '{SubscriptionName}': {ExceptionType}, " +
                  "StatusCode={StatusCode}. Retrying.")]
    private partial void LogPollingError(string subscriptionName, string exceptionType, string statusCode);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Pub/Sub polling fatal error for subscription '{SubscriptionName}': {ExceptionType}, " +
                  "StatusCode={StatusCode}. Consumer loop exiting.")]
    private partial void LogPollingFatalError(string subscriptionName, string exceptionType, string statusCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub in-flight registry rejected a received message for subscription '{SubscriptionName}' — " +
                  "returning {Count} message(s) to the broker so they become deliverable again.")]
    private partial void LogRegistryFull(int count, string subscriptionName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "In-flight registry or endpoint buffer full for subscription '{SubscriptionName}'; pausing pull.")]
    private partial void LogReceivePaused(string subscriptionName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub channel full (Drop mode) — dropping message DeliveryTag={DeliveryTag} for subscription '{SubscriptionName}'.")]
    private partial void LogMessageDropped(ulong deliveryTag, string subscriptionName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Pub/Sub Reject (DeliveryTag={DeliveryTag}): leaving message for DeadLetterPolicy DLQ (ADR-017).")]
    private partial void LogRejectViaDeadLetterPolicy(ulong deliveryTag);
}
