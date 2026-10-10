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
internal sealed partial class AzureServiceBusConsumer : IAsyncDisposable, IAzureServiceBusReceiveControl
{
    private readonly ServiceBusReceiver _receiver;
    private readonly Channel<InboundMessage> _channel;
    private readonly AzureServiceBusConsumerRegistry _registry;
    private readonly string _consumerId;
    private readonly string _endpointName;
    private readonly ILogger _logger;

    private ulong _deliveryTagCounter;
    private readonly AzureServiceBusShutdownBudget _shutdownBudget;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Task? _receiveStopTask;
    private Task? _stopTask;
    private bool _disposed;

    /// <summary>Time shared by every broker call made while this consumer hands its messages back.</summary>
    internal static readonly TimeSpan ShutdownDrainBudget = TimeSpan.FromSeconds(5);

    internal AzureServiceBusConsumer(
        ServiceBusReceiver receiver,
        Channel<InboundMessage> channel,
        AzureServiceBusConsumerRegistry registry,
        string consumerId,
        string endpointName,
        ILogger logger,
        TimeSpan? shutdownBudget = null)
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
        _shutdownBudget = new AzureServiceBusShutdownBudget(shutdownBudget ?? ShutdownDrainBudget);
    }

    /// <summary>Gets the unique id of this consumer instance.</summary>
    internal string ConsumerId => _consumerId;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the polling loop on a dedicated long-running task (D-1). The loop token is linked to
    /// <paramref name="consumeToken"/>, so receiving stops the moment the consumer is cancelled — before
    /// the caller settles the messages it still holds.
    /// </summary>
    internal void StartLoop(CancellationToken consumeToken)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(consumeToken);
        CancellationToken loopToken = _loopCts.Token;

        // D-1: dedicated long-running task so the async poll does not occupy a thread-pool thread
        // for the full lifetime of the consumer when many consumers are active.
        // The scheduling token is deliberately not passed: the loop body must always run to its finally.
        _loopTask = Task.Factory.StartNew(
            async () => await RunLoopAsync(loopToken).ConfigureAwait(false),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    /// <inheritdoc />
    public bool IsStopRequested => _loopCts?.IsCancellationRequested ?? false;

    /// <inheritdoc />
    public Task EnsureReceiveStoppedAsync() =>
        SingleFlight(ref _receiveStopTask, StopReceivingAsync);

    /// <summary>
    /// Stops receiving, hands back what is buffered, unregisters this consumer and closes the receiver.
    /// Order: stop receive loop, complete the writer, drain (evict, abandon within the shared budget,
    /// dispose), unregister, close. Single-flight and never throws.
    /// </summary>
    internal Task StopAsync() => SingleFlight(ref _stopTask, StopCoreAsync);

    private async Task StopReceivingAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_loopTask is null)
        {
            return;
        }

        try
        {
            await _loopTask.WaitAsync(AzureServiceBusShutdownDrain.ReceiveStopTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — the loop was cancelled.
        }
        catch (TimeoutException)
        {
            LogReceiveStopTimedOut(_consumerId, _endpointName);
        }
        catch (Exception ex)
        {
            AzureServiceBusErrorInfo error = AzureServiceBusErrorInfo.From(ex);
            LogLoopStopError(error.ExceptionType, error.FailureReason);
        }
    }

    private async Task StopCoreAsync()
    {
        await EnsureReceiveStoppedAsync().ConfigureAwait(false);

        _channel.Writer.TryComplete();

        await AzureServiceBusShutdownDrain.DrainAsync(
            _channel.Reader, _registry, _consumerId, _endpointName, abandon: true, _shutdownBudget, _logger)
            .ConfigureAwait(false);

        _registry.Unregister(_consumerId);

        try
        {
            await _receiver.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AzureServiceBusErrorInfo error = AzureServiceBusErrorInfo.From(ex);
            LogReceiverCloseError(error.ExceptionType, error.FailureReason);
        }
    }

    // Runs 'work' at most once; every caller gets the same task, which never faults ('work' handles its errors).
    private static Task SingleFlight(ref Task? slot, Func<Task> work)
    {
        if (Volatile.Read(ref slot) is { } existing)
        {
            return existing;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref slot, completion.Task, null) is { } prior)
        {
            return prior;
        }

        _ = RunAsync(work, completion);
        return completion.Task;

        static async Task RunAsync(Func<Task> work, TaskCompletionSource completion)
        {
            try
            {
                await work().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
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
        _shutdownBudget.Dispose();
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
                    AzureServiceBusErrorInfo error = AzureServiceBusErrorInfo.From(ex);
                    LogReceiveError(error.ExceptionType, error.FailureReason);
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

                for (int i = 0; i < batch.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Received but never registered: hand the rest of the batch back right away
                        // instead of leaving it locked until the lock expires.
                        await AzureServiceBusShutdownDrain.AbandonUnregisteredAsync(
                            _receiver, batch, i, _endpointName, _shutdownBudget, _logger).ConfigureAwait(false);
                        return;
                    }

                    ServiceBusReceivedMessage received = batch[i];
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
                    catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
                    {
                        // Nobody will consume this message or the rest of the batch: release them all.
                        _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                        message.Dispose();
                        await AzureServiceBusShutdownDrain.AbandonUnregisteredAsync(
                            _receiver, batch, i, _endpointName, _shutdownBudget, _logger).ConfigureAwait(false);
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
        Message = "Azure Service Bus consumer: error receiving messages ({ExceptionType}, Reason={FailureReason}). Will retry after back-off.")]
    private partial void LogReceiveError(string exceptionType, string failureReason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus consumer loop terminated with an unexpected error ({ExceptionType}, Reason={FailureReason}).")]
    private partial void LogLoopStopError(string exceptionType, string failureReason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus receiver Close() threw an exception during shutdown ({ExceptionType}, Reason={FailureReason}).")]
    private partial void LogReceiverCloseError(string exceptionType, string failureReason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus consumer {ConsumerId} on queue '{QueueName}': the receive loop did not stop in time; continuing shutdown.")]
    private partial void LogReceiveStopTimedOut(string consumerId, string queueName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus consumer {ConsumerId} on queue '{QueueName}': message DeliveryTag={DeliveryTag} dropped by the bounded channel (non-Wait FullMode); registry entry evicted, PeekLock will expire and the message will be redelivered.")]
    private partial void LogMessageDropped(string consumerId, string queueName, ulong deliveryTag);
}
