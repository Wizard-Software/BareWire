using System.Buffers;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// Session-aware consumer that accepts Azure Service Bus sessions via
/// <c>ServiceBusClient.AcceptNextSessionAsync</c> and bridges each session's messages into a
/// bounded <see cref="Channel{T}"/> of <see cref="InboundMessage"/> with per-session FIFO ordering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-session channel topology (D-9/PERF-1):</b> Each accepted
/// <see cref="ServiceBusSessionReceiver"/> writes to its OWN bounded channel
/// (<c>SingleWriter = true</c>, legal because exactly one session-reader task is the sole writer).
/// This guarantees FIFO ordering within each <c>SessionId</c> and makes the <c>SingleWriter</c>
/// contract safe at any <c>MaxConcurrentSessions</c> value. A shared channel with
/// <c>SingleWriter = true</c> written by N parallel session tasks would violate the contract
/// and corrupt ordering.
/// </para>
/// <para>
/// <b>Accept-side concurrency gate (D-12/VER-3):</b> A <see cref="SemaphoreSlim"/> with
/// initial count equal to <c>MaxConcurrentSessions</c> gates the outer accept loop. Each
/// iteration acquires a permit BEFORE calling <c>AcceptNextSessionAsync</c> and releases it
/// in a <c>finally</c> block that covers the accept call itself — so a
/// <see cref="ServiceBusFailureReason.ServiceTimeout"/> exception (thrown routinely when no
/// session is available) never leaks a permit.
/// </para>
/// <para>
/// <b>Background session-lock renew (D-10/PERF-2):</b> For each active session, a separate
/// long-running task proactively calls
/// <c>ServiceBusSessionReceiver.RenewSessionLockAsync()</c> at an interval derived from
/// <c>SessionReceiver.SessionLockedUntil</c> (≈ half the remaining lock window, minus a
/// 10-second safety margin). This prevents <c>SessionLockLost</c> while the receive loop is
/// blocked under channel back-pressure. <c>MaxAutoLockRenewDuration</c> bounds the total
/// elapsed renew time per session (<c>TimeSpan.Zero</c> disables background renew).
/// </para>
/// <para>
/// <b>Session-lock release and cleanup (D-11/VER-2):</b> When a session ends (idle drain or
/// <c>SessionLockLost</c>), the cleanup sequence is:
/// <list type="number">
/// <item><description>Cancel the per-session <see cref="CancellationTokenSource"/> (stops both the receive loop and the renew task).</description></item>
/// <item><description>Call <see cref="AzureServiceBusConsumerRegistry.EvictAllForSession"/> to bulk-remove all in-flight delivery-tag entries for the session.</description></item>
/// <item><description>Release the <see cref="SemaphoreSlim"/> permit.</description></item>
/// <item><description>Dispose the <see cref="ServiceBusSessionReceiver"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>SDK thread-safety:</b> <see cref="ServiceBusReceiver"/> (and its subclass
/// <see cref="ServiceBusSessionReceiver"/>) is documented as thread-safe for concurrent
/// operations. The receive loop and renew task therefore share the same receiver instance safely.
/// </para>
/// </remarks>
internal sealed partial class AzureServiceBusSessionConsumer : IAsyncDisposable
{
    // Minimum sleep floor in the renew loop, avoiding busy-spinning when locked-until is in the past.
    private static readonly TimeSpan RenewMinFloor = TimeSpan.FromSeconds(2);
    // Safety margin subtracted from the remaining lock window before sleeping in the renew loop.
    private static readonly TimeSpan RenewSafetyMargin = TimeSpan.FromSeconds(10);

    private readonly ServiceBusClient _client;
    private readonly string _endpointName;
    private readonly AzureServiceBusTransportOptions _options;
    private readonly AzureServiceBusConsumerRegistry _registry;
    private readonly string _consumerId;
    private readonly Channel<InboundMessage> _outputChannel;
    private readonly FlowControlOptions _flowControl;
    private readonly ILogger _logger;

    // Accept-side concurrency gate (D-12/VER-3).
    private readonly SemaphoreSlim _acceptGate;

    private ulong _deliveryTagCounter;
    private CancellationTokenSource? _outerCts;
    private Task? _outerTask;
    private bool _disposed;

    internal AzureServiceBusSessionConsumer(
        ServiceBusClient client,
        string endpointName,
        AzureServiceBusTransportOptions options,
        AzureServiceBusConsumerRegistry registry,
        string consumerId,
        Channel<InboundMessage> outputChannel,
        FlowControlOptions flowControl,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(consumerId);
        ArgumentNullException.ThrowIfNull(outputChannel);
        ArgumentNullException.ThrowIfNull(flowControl);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _endpointName = endpointName;
        _options = options;
        _registry = registry;
        _consumerId = consumerId;
        _outputChannel = outputChannel;
        _flowControl = flowControl;
        _logger = logger;

        _acceptGate = new SemaphoreSlim(options.MaxConcurrentSessions, options.MaxConcurrentSessions);
    }

    /// <summary>Gets the unique consumer id stamped into all inbound messages from this consumer.</summary>
    internal string ConsumerId => _consumerId;

    /// <summary>
    /// Exposes the accept-gate semaphore for unit testing of the concurrency-bound invariant (D-12).
    /// </summary>
    internal SemaphoreSlim AcceptGate => _acceptGate;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the outer accept loop on a dedicated long-running task.
    /// </summary>
    internal void StartLoop()
    {
        _outerCts = new CancellationTokenSource();

        _outerTask = Task.Factory.StartNew(
            async () => await RunAcceptLoopAsync(_outerCts.Token).ConfigureAwait(false),
            _outerCts.Token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Signals the outer accept loop to stop and waits for it to finish.
    /// Completes the output channel writer so callers draining the channel receive EOF.
    /// </summary>
    internal async Task StopAsync()
    {
        if (_outerCts is not null)
        {
            await _outerCts.CancelAsync().ConfigureAwait(false);
        }

        if (_outerTask is not null)
        {
            try
            {
                await _outerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — the outer loop was cancelled.
            }
            catch (Exception ex)
            {
                LogOuterLoopStopError(ex);
            }
        }

        _outputChannel.Writer.TryComplete();
        _registry.Unregister(_consumerId);
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

        _outerCts?.Dispose();
        _acceptGate.Dispose();
    }

    // ── Accept loop ───────────────────────────────────────────────────────────

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        LogSessionConsumerStarted(_consumerId, _endpointName);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // D-12/VER-3: acquire a permit BEFORE AcceptNextSessionAsync.
                // The try/finally MUST cover AcceptNextSessionAsync itself so that the
                // ServiceTimeout exception (thrown routinely when no session is available)
                // releases the permit and never leaks (PERF-leak, runda 3).
                await _acceptGate.WaitAsync(cancellationToken).ConfigureAwait(false);

                ServiceBusSessionReceiver? sessionReceiver = null;

                try
                {
                    var sessionReceiverOptions = new ServiceBusSessionReceiverOptions
                    {
                        ReceiveMode = ServiceBusReceiveMode.PeekLock,
                        PrefetchCount = _options.PrefetchCount,
                    };

                    sessionReceiver = await _client.AcceptNextSessionAsync(
                        _endpointName,
                        sessionReceiverOptions,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (ServiceBusException sbEx)
                    when (sbEx.Reason == ServiceBusFailureReason.ServiceTimeout)
                {
                    // Routinely thrown when no session is available within the SDK's try-timeout.
                    // Back off briefly and retry — this is not an error.
                    _acceptGate.Release();

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
                catch (OperationCanceledException)
                {
                    _acceptGate.Release();
                    break;
                }
                catch (Exception ex)
                {
                    _acceptGate.Release();
                    LogAcceptSessionError(ex);

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                // Accepted a session — process it on a separate long-running task so the accept
                // loop can immediately go back and accept the next session (up to MaxConcurrentSessions).
                string sessionId = sessionReceiver.SessionId;
                LogSessionAccepted(_consumerId, _endpointName, sessionId);

                // Capture for the lambda below (avoids capturing the loop variable).
                ServiceBusSessionReceiver capturedReceiver = sessionReceiver;
                CancellationToken outerToken = cancellationToken;

                // PERF-3/ASSM-1: guard against a synchronous throw from StartNew before the
                // lambda runs. If dispatch fails, release the permit and dispose the just-accepted
                // receiver so neither the AMQP link nor the semaphore slot leaks.
                // On the success path the permit is released exactly once by the task's finally.
                bool dispatched = false;
                try
                {
                    _ = Task.Factory.StartNew(
                        async () =>
                        {
                            try
                            {
                                await ProcessSessionAsync(capturedReceiver, sessionId, outerToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                _acceptGate.Release();
                            }
                        },
                        outerToken,
                        TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default).Unwrap();

                    dispatched = true;
                }
                catch (Exception ex)
                {
                    // Dispatch failed synchronously — release the permit and dispose the receiver
                    // to avoid leaking either resource. Do NOT re-throw: the outer accept loop
                    // should continue accepting sessions if possible.
                    _acceptGate.Release();

                    try
                    {
                        await capturedReceiver.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeEx)
                    {
                        LogSessionReceiverDisposeError(disposeEx, sessionId);
                    }

                    LogAcceptSessionError(ex);
                }

                _ = dispatched; // suppress unused-variable warning
            }
        }
        finally
        {
            LogSessionConsumerStopped(_consumerId, _endpointName);
        }
    }

    // ── Per-session processing ─────────────────────────────────────────────────

    private async Task ProcessSessionAsync(
        ServiceBusSessionReceiver sessionReceiver,
        string sessionId,
        CancellationToken outerCancellationToken)
    {
        // Per-session bounded channel (D-9/PERF-1):
        // SingleWriter = true is legal because this session task is the ONLY writer.
        // FIFO within the session is guaranteed by the sequential single-reader drain below.
        // FullMode is pinned to Wait via BuildSessionChannelOptions — Drop* modes void per-session FIFO.
        Channel<InboundMessage> sessionChannel = Channel.CreateBounded<InboundMessage>(
            BuildSessionChannelOptions(_flowControl.InternalQueueCapacity));

        // Per-session CTS — shared by the receive loop and the renew task.
        using CancellationTokenSource sessionCts =
            CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);

        CancellationToken sessionToken = sessionCts.Token;

        // Start background renew task BEFORE the receive loop (D-10/PERF-2).
        Task renewTask = Task.CompletedTask;
        if (_options.MaxAutoLockRenewDuration > TimeSpan.Zero)
        {
            renewTask = Task.Factory.StartNew(
                async () => await RunRenewLoopAsync(sessionReceiver, sessionId, sessionCts, sessionToken).ConfigureAwait(false),
                sessionToken,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
        }

        // Drain task: reads from the per-session channel and forwards to the shared output channel.
        Task drainTask = Task.Factory.StartNew(
            async () => await DrainSessionChannelAsync(sessionChannel, sessionToken).ConfigureAwait(false),
            sessionToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();

        bool sessionLockLost = false;

        try
        {
            await RunSessionReceiveLoopAsync(sessionReceiver, sessionId, sessionChannel, sessionToken).ConfigureAwait(false);
        }
        catch (ServiceBusException sbEx) when (sbEx.Reason == ServiceBusFailureReason.SessionLockLost)
        {
            // SessionLockLost — safety net (D-10 proactive renew is primary).
            sessionLockLost = true;
            LogSessionLockLost(_consumerId, _endpointName, sessionId);
        }
        catch (OperationCanceledException)
        {
            // Outer cancellation — normal shutdown.
        }
        catch (Exception ex)
        {
            LogSessionReceiveError(ex, sessionId);
        }
        finally
        {
            // Cleanup sequence (D-11/VER-2):
            // 1. Cancel the session CTS (stops receive loop and renew task peer).
            await sessionCts.CancelAsync().ConfigureAwait(false);

            // 2. Wait for renew task to finish.
            try
            {
                await renewTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogRenewTaskError(ex, sessionId); }

            // 3. Complete the per-session channel so the drain task terminates.
            sessionChannel.Writer.TryComplete();

            // 4. Wait for drain task to finish.
            try
            {
                await drainTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogDrainTaskError(ex, sessionId); }

            // 5. Bulk-evict all in-flight registry entries for this session (D-11/VER-2).
            _registry.EvictAllForSession(_consumerId, sessionId);

            // 6. Dispose the session receiver.
            try
            {
                await sessionReceiver.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSessionReceiverDisposeError(ex, sessionId);
            }

            LogSessionReleased(_consumerId, _endpointName, sessionId, sessionLockLost);
        }
    }

    // ── Session receive loop ──────────────────────────────────────────────────

    private async Task RunSessionReceiveLoopAsync(
        ServiceBusSessionReceiver sessionReceiver,
        string sessionId,
        Channel<InboundMessage> sessionChannel,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<ServiceBusReceivedMessage> batch;

            try
            {
                batch = await sessionReceiver.ReceiveMessagesAsync(
                    maxMessages: 10,
                    maxWaitTime: _options.SessionIdleTimeout ?? TimeSpan.FromSeconds(1),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ServiceBusException sbEx) when (sbEx.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                throw; // Re-throw so the outer catch in ProcessSessionAsync handles cleanup.
            }
            catch (Exception ex)
            {
                LogReceiveError(ex, sessionId);
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
                // No messages in the session — idle drain complete; release the session.
                break;
            }

            foreach (ServiceBusReceivedMessage received in batch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                InboundMessage message = BuildMessage(received, sessionReceiver, sessionId);

                // Back-pressure: block until the per-session channel can accept a write.
                // PERF-1 drop-detection: check TryWrite result.
                try
                {
                    bool canWrite = await sessionChannel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);

                    if (!canWrite || !sessionChannel.Writer.TryWrite(message))
                    {
                        _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                        message.Dispose();
                        LogMessageDropped(_consumerId, _endpointName, message.DeliveryTag, sessionId);
                    }
                }
                catch (OperationCanceledException)
                {
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

    // ── Drain task (per-session channel → shared output channel) ─────────────

    private async Task DrainSessionChannelAsync(
        Channel<InboundMessage> sessionChannel,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (InboundMessage message in sessionChannel.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Forward into the shared output channel.
                try
                {
                    bool canWrite = await _outputChannel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);

                    if (!canWrite || !_outputChannel.Writer.TryWrite(message))
                    {
                        _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                        message.Dispose();
                    }
                }
                catch (OperationCanceledException)
                {
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
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    // ── Background renew loop (D-10 / PERF-2 / Opcja A) ─────────────────────

    private async Task RunRenewLoopAsync(
        ServiceBusSessionReceiver sessionReceiver,
        string sessionId,
        CancellationTokenSource sessionCts,
        CancellationToken cancellationToken)
    {
        DateTimeOffset renewStart = DateTimeOffset.UtcNow;
        TimeSpan maxDuration = _options.MaxAutoLockRenewDuration;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Check total elapsed renew time.
                if (maxDuration > TimeSpan.Zero &&
                    DateTimeOffset.UtcNow - renewStart >= maxDuration)
                {
                    LogRenewDurationExceeded(_consumerId, _endpointName, sessionId);
                    break;
                }

                // Compute sleep interval from the live SessionLockedUntil value (D-10 PERF-2).
                // MUST use SessionLockedUntil, NOT MaxAutoLockRenewDuration, as the sleep interval —
                // confusing these would cause the lock to expire before the first renew.
                DateTimeOffset lockedUntil = sessionReceiver.SessionLockedUntil;
                TimeSpan remaining = lockedUntil - DateTimeOffset.UtcNow - RenewSafetyMargin;
                TimeSpan sleepInterval = remaining / 2;

                if (sleepInterval < RenewMinFloor)
                {
                    sleepInterval = RenewMinFloor;
                }

                try
                {
                    await Task.Delay(sleepInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await sessionReceiver.RenewSessionLockAsync(cancellationToken).ConfigureAwait(false);
                    LogSessionLockRenewed(_consumerId, _endpointName, sessionId, sessionReceiver.SessionLockedUntil);
                }
                catch (ServiceBusException sbEx) when (sbEx.Reason == ServiceBusFailureReason.SessionLockLost)
                {
                    // Lock already expired — stop renewing; the receive loop will detect and handle.
                    LogSessionLockLostInRenew(_consumerId, _endpointName, sessionId);
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogRenewError(ex, sessionId);
                    // Continue retrying — transient error.
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Session channel factory ───────────────────────────────────────────────

    /// <summary>
    /// Builds <see cref="BoundedChannelOptions"/> for a per-session channel.
    /// </summary>
    /// <param name="capacity">Channel capacity from <see cref="FlowControlOptions.InternalQueueCapacity"/>.</param>
    /// <returns>Options with <see cref="BoundedChannelFullMode.Wait"/> pinned unconditionally.</returns>
    /// <remarks>
    /// Drop* modes (DropWrite / DropOldest / DropNewest) void the per-session FIFO guarantee
    /// (D-9/PERF-1): a mid-session message could be silently dropped while later messages
    /// still enqueue, creating a hole in the ordered sequence within a SessionId. The session
    /// path therefore always uses Wait, regardless of the caller's FlowControlOptions.FullMode.
    /// </remarks>
    internal static BoundedChannelOptions BuildSessionChannelOptions(int capacity) =>
        new(capacity)
        {
            // Drop* modes void per-session FIFO (D-9/PERF-1), so the session path pins Wait.
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        };

    // ── Message construction ──────────────────────────────────────────────────

    private InboundMessage BuildMessage(
        ServiceBusReceivedMessage received,
        ServiceBusSessionReceiver sessionReceiver,
        string sessionId)
    {
        // D-4 (zero-copy body): BinaryData.ToMemory() wraps the SDK's internal buffer without copying.
        ReadOnlyMemory<byte> bodyMemory = received.Body.ToMemory();
        ReadOnlySequence<byte> body = bodyMemory.Length > 0
            ? new ReadOnlySequence<byte>(bodyMemory)
            : ReadOnlySequence<byte>.Empty;

        Dictionary<string, string> headers = AzureServiceBusHeaderMapper.MapInbound(
            received.ApplicationProperties);

        // Stamp BareWire routing headers AFTER MapInbound (last-write-wins, anti-spoof).
        headers["BW-ConsumerId"] = _consumerId;
        headers["BW-Queue"] = _endpointName;

        // D-5: stamp BW-SessionId from the native field AFTER MapInbound so it cannot be
        // spoofed via ApplicationProperties (mirrors BW-ConsumerId / BW-Queue pattern).
        headers[AzureServiceBusHeaderMapper.SessionIdHeader] = sessionId;

        string messageId = !string.IsNullOrEmpty(received.MessageId)
            ? received.MessageId
            : Guid.NewGuid().ToString("N");

        // D-2 (corrected): per-consumer monotonic ulong; matches InboundMessage.DeliveryTag type.
        ulong deliveryTag = Interlocked.Increment(ref _deliveryTagCounter);

        // Store DeliveryTag → (message, sessionReceiver) — sessionReceiver inherits from
        // ServiceBusReceiver so settlement methods work polymorphically (D-3/OQ-1).
        // Pass sessionId so EvictAllForSession can find entries by session (D-11/VER-2).
        _registry.StoreMessage(_consumerId, deliveryTag, received, sessionReceiver, sessionId);

        return new InboundMessage(
            messageId: messageId,
            headers: headers,
            body: body,
            deliveryTag: deliveryTag,
            pooledBuffer: null);
    }

    // ── Logging (source-gen partial methods) ──────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus session consumer {ConsumerId} started accepting sessions on queue '{QueueName}'.")]
    private partial void LogSessionConsumerStarted(string consumerId, string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus session consumer {ConsumerId} stopped on queue '{QueueName}'.")]
    private partial void LogSessionConsumerStopped(string consumerId, string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus session consumer {ConsumerId} accepted session '{SessionId}' on queue '{QueueName}'.")]
    private partial void LogSessionAccepted(string consumerId, string queueName, string sessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus session consumer {ConsumerId} released session '{SessionId}' on queue '{QueueName}'. LockLost={LockLost}.")]
    private partial void LogSessionReleased(string consumerId, string queueName, string sessionId, bool lockLost);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus session consumer {ConsumerId}: session lock lost for session '{SessionId}' on queue '{QueueName}'. Releasing session.")]
    private partial void LogSessionLockLost(string consumerId, string queueName, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus session consumer {ConsumerId}: session lock lost during renew for session '{SessionId}' on queue '{QueueName}'. Stopping renew.")]
    private partial void LogSessionLockLostInRenew(string consumerId, string queueName, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Azure Service Bus session consumer {ConsumerId}: session lock renewed for session '{SessionId}' on queue '{QueueName}'. LockedUntil={LockedUntil}.")]
    private partial void LogSessionLockRenewed(string consumerId, string queueName, string sessionId, DateTimeOffset lockedUntil);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus session consumer {ConsumerId}: MaxAutoLockRenewDuration exceeded for session '{SessionId}' on queue '{QueueName}'. Background renew stopped.")]
    private partial void LogRenewDurationExceeded(string consumerId, string queueName, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error accepting session on queue. Will back off and retry.")]
    private partial void LogAcceptSessionError(Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error receiving messages for session '{SessionId}'. Will retry after back-off.")]
    private partial void LogReceiveError(Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: unexpected error in session '{SessionId}' receive loop.")]
    private partial void LogSessionReceiveError(Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error in renew task for session '{SessionId}'.")]
    private partial void LogRenewError(Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error in renew task for session '{SessionId}' during shutdown.")]
    private partial void LogRenewTaskError(Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error in drain task for session '{SessionId}' during shutdown.")]
    private partial void LogDrainTaskError(Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error disposing session receiver for session '{SessionId}'.")]
    private partial void LogSessionReceiverDisposeError(Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer loop terminated with an unexpected error.")]
    private partial void LogOuterLoopStopError(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus session consumer {ConsumerId} on queue '{QueueName}': message DeliveryTag={DeliveryTag} dropped by the per-session bounded channel (non-Wait FullMode); registry entry evicted. SessionId='{SessionId}'.")]
    private partial void LogMessageDropped(string consumerId, string queueName, ulong deliveryTag, string sessionId);
}
