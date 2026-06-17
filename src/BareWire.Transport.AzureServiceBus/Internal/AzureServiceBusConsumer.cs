using System.Buffers;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// Bridges a <see cref="ServiceBusReceiver"/> PeekLock polling loop into a bounded
/// <see cref="Channel{T}"/> of <see cref="InboundMessage"/> instances.
/// Mirrors <c>KafkaConsumer</c> in threading model and channel back-pressure strategy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading model (D-1):</b> The async <c>receiver.ReceiveMessagesAsync</c> polling loop
/// runs on a dedicated long-running <see cref="Task"/> (via <see cref="TaskCreationOptions.LongRunning"/>)
/// to avoid occupying thread-pool threads for the lifetime of each consumer.
/// </para>
/// <para>
/// <b>DeliveryTag (D-2 corrected):</b> A per-consumer monotonic <c>ulong</c> incremented by
/// <c>Interlocked.Increment</c>. The <see cref="ServiceBusReceivedMessage.SequenceNumber"/>
/// is scoped to the entity, not to the consumer; using a monotonic counter avoids collisions
/// when multiple consumers read the same queue and matches <see cref="InboundMessage.DeliveryTag"/>
/// (<c>ulong</c>). The <see cref="ServiceBusReceivedMessage"/> itself (needed for settlement)
/// is stored in <see cref="AzureServiceBusConsumerRegistry"/> keyed by this tag.
/// </para>
/// <para>
/// <b>Body ownership (D-4 / zero-copy):</b>
/// <c>ServiceBusReceivedMessage.Body.ToMemory()</c> returns a <see cref="ReadOnlyMemory{T}"/>
/// that wraps the SDK's internal buffer without copying. Wrapping this in a
/// <see cref="ReadOnlySequence{T}"/> incurs no additional allocation. <c>pooledBuffer</c> is
/// therefore <see langword="null"/>; <see cref="InboundMessage.Dispose"/> is a no-op for the body.
/// </para>
/// <para>
/// <b>Correctness invariant (R-4):</b> The zero-copy wrap remains valid for the lifetime of the
/// <see cref="ServiceBusReceivedMessage"/>. The message must remain registered in
/// <see cref="AzureServiceBusConsumerRegistry"/> until <c>SettleAsync</c> evicts it — do NOT
/// remove the registry entry before settlement.
/// </para>
/// <para>
/// <b>Back-pressure:</b> The bounded channel with <c>FullMode.Wait</c> applies back-pressure by
/// blocking the polling loop when the channel is full. ASB PeekLock does not require an explicit
/// pause/resume call (unlike Kafka partition pause) — stopping <c>ReceiveMessagesAsync</c> simply
/// delays the next pull, which is the natural back-pressure mechanism for a pull-based API.
/// </para>
/// <para>
/// <b>Batch semantics:</b> <c>ReceiveMessagesAsync(maxMessages, maxWaitTime, ct)</c> does NOT
/// guarantee returning a full batch — it may return fewer messages than requested. Each message
/// in the partial batch is processed individually (do NOT assume a full batch was returned, R-4).
/// </para>
/// </remarks>
internal sealed partial class AzureServiceBusConsumer : IAsyncDisposable
{
    private readonly ServiceBusReceiver _receiver;
    private readonly Channel<InboundMessage> _channel;
    private readonly AzureServiceBusConsumerRegistry _registry;
    private readonly string _consumerId;
    private readonly string _endpointName;
    private readonly ILogger _logger;

    private ulong _deliveryTagCounter;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    internal AzureServiceBusConsumer(
        ServiceBusReceiver receiver,
        Channel<InboundMessage> channel,
        AzureServiceBusConsumerRegistry registry,
        string consumerId,
        string endpointName,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(consumerId);
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(logger);

        _receiver = receiver;
        _channel = channel;
        _registry = registry;
        _consumerId = consumerId;
        _endpointName = endpointName;
        _logger = logger;
    }

    /// <summary>Gets the unique id of this consumer instance.</summary>
    internal string ConsumerId => _consumerId;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the polling loop on a dedicated long-running task (D-1).
    /// </summary>
    internal void StartLoop()
    {
        _loopCts = new CancellationTokenSource();

        // D-1: dedicated long-running task so the async poll does not occupy a thread-pool thread
        // for the full lifetime of the consumer when many consumers are active.
        _loopTask = Task.Factory.StartNew(
            async () => await RunLoopAsync(_loopCts.Token).ConfigureAwait(false),
            _loopCts.Token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Signals the polling loop to stop, completes the channel writer, unregisters this
    /// consumer from the registry, and closes the receiver.
    /// </summary>
    internal async Task StopAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — the loop was cancelled.
            }
            catch (Exception ex)
            {
                LogLoopStopError(ex);
            }
        }

        _channel.Writer.TryComplete();
        _registry.Unregister(_consumerId);

        try
        {
            await _receiver.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReceiverCloseError(ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        await _receiver.DisposeAsync().ConfigureAwait(false);
        _loopCts?.Dispose();
    }

    // ── Polling loop ──────────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        LogConsumerStarted(_consumerId, _endpointName);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<ServiceBusReceivedMessage> batch;

                try
                {
                    // Pull-based: ASB does not guarantee returning maxMessages entries —
                    // process whatever was returned without assuming a full batch (R-4).
                    batch = await _receiver.ReceiveMessagesAsync(
                        maxMessages: 10,
                        maxWaitTime: TimeSpan.FromSeconds(1),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogReceiveError(ex);
                    // Transient error — back off briefly and retry.
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                if (batch.Count == 0)
                {
                    // No messages available — loop back and poll again.
                    continue;
                }

                foreach (ServiceBusReceivedMessage received in batch)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    InboundMessage message = BuildMessage(received);

                    // Back-pressure: block the loop until the bounded channel can accept a write.
                    // Under FullMode.Wait this suspends the loop when capacity is exhausted, preventing
                    // over-fetching. Under DropWrite/DropOldest/DropNewest, WaitToWriteAsync returns
                    // true immediately and TryWrite may return false — that is the drop-detection path.
                    try
                    {
                        bool canWrite = await _channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);

                        // PERF-1: check the result of TryWrite. Under a non-Wait FullMode
                        // (DropWrite/DropOldest/DropNewest — all publicly settable on
                        // FlowControlOptions.FullMode), WaitToWriteAsync returns true immediately and
                        // TryWrite silently drops/evicts the message. Without this guard the dropped
                        // message would (a) never be settled → PeekLock expires → DLQ after
                        // max-delivery-count, and (b) leave an orphaned registry entry that grows
                        // without bound, violating the CONSTITUTION "no unbounded buffers" rule.
                        if (!canWrite || !_channel.Writer.TryWrite(message))
                        {
                            _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                            message.Dispose();
                            LogMessageDropped(_consumerId, _endpointName, message.DeliveryTag);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Disposing the message — nobody will consume it.
                        _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                        message.Dispose();
                        return;
                    }
                    catch (ChannelClosedException)
                    {
                        _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                        message.Dispose();
                        return;
                    }
                }
            }
        }
        finally
        {
            LogConsumerStopped(_consumerId, _endpointName);
        }
    }

    // ── Message construction ──────────────────────────────────────────────────

    private InboundMessage BuildMessage(ServiceBusReceivedMessage received)
    {
        // D-4 (zero-copy body): BinaryData.ToMemory() wraps the SDK's internal buffer without
        // copying. Wrapping in ReadOnlySequence<byte> adds no allocation. pooledBuffer = null.
        // Correctness (R-4): the wrap stays valid as long as 'received' is retained in the
        // registry. Do NOT evict the registry entry before SettleAsync completes.
        ReadOnlyMemory<byte> bodyMemory = received.Body.ToMemory();
        ReadOnlySequence<byte> body = bodyMemory.Length > 0
            ? new ReadOnlySequence<byte>(bodyMemory)
            : ReadOnlySequence<byte>.Empty;

        Dictionary<string, string> headers = AzureServiceBusHeaderMapper.MapInbound(
            received.ApplicationProperties);

        // Stamp BareWire routing headers AFTER MapInbound (last-write-wins, mirrors KafkaConsumer D5)
        // so that wire-level BW-ConsumerId cannot spoof the consumer identity.
        headers["BW-ConsumerId"] = _consumerId;
        headers["BW-Queue"] = _endpointName;

        string messageId = !string.IsNullOrEmpty(received.MessageId)
            ? received.MessageId
            : Guid.NewGuid().ToString("N");

        // D-2 (corrected): per-consumer monotonic ulong; matches InboundMessage.DeliveryTag type.
        ulong deliveryTag = Interlocked.Increment(ref _deliveryTagCounter);

        // Store DeliveryTag → (message, receiver) so SettleAsync can execute the settlement.
        _registry.StoreMessage(_consumerId, deliveryTag, received, _receiver);

        return new InboundMessage(
            messageId: messageId,
            headers: headers,
            body: body,
            deliveryTag: deliveryTag,
            pooledBuffer: null);
    }

    // ── Logging (source-gen partial methods) ──────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus consumer {ConsumerId} started polling queue '{QueueName}'.")]
    private partial void LogConsumerStarted(string consumerId, string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus consumer {ConsumerId} stopped polling queue '{QueueName}'.")]
    private partial void LogConsumerStopped(string consumerId, string queueName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus consumer: error receiving messages. Will retry after back-off.")]
    private partial void LogReceiveError(Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus consumer loop terminated with an unexpected error.")]
    private partial void LogLoopStopError(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus receiver Close() threw an exception during shutdown.")]
    private partial void LogReceiverCloseError(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus consumer {ConsumerId} on queue '{QueueName}': message DeliveryTag={DeliveryTag} dropped by the bounded channel (non-Wait FullMode); registry entry evicted, PeekLock will expire and the message will be redelivered.")]
    private partial void LogMessageDropped(string consumerId, string queueName, ulong deliveryTag);
}
