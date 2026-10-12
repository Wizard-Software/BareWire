using System.Buffers;
using System.Collections.Concurrent;
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
/// <b>Per-session channel topology:</b> Each accepted
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
/// <item><description>When the session went idle while forwarded messages are still unsettled, stop receiving, keep the receiver and its lock renewal alive, and wait for those messages to be settled (see below).</description></item>
/// <item><description>Cancel the per-session <see cref="CancellationTokenSource"/> (stops both the renew task and the forwarding task).</description></item>
/// <item><description>Call <see cref="AzureServiceBusConsumerRegistry.EvictAllForSession"/> to bulk-remove all remaining delivery-tag entries for the session.</description></item>
/// <item><description>Release the <see cref="SemaphoreSlim"/> permit.</description></item>
/// <item><description>Dispose the <see cref="ServiceBusSessionReceiver"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Idle exit with messages in flight:</b> The settlement wait does not issue new receive calls, and the
/// session stays locked, so no other consumer can take it while older messages of the session are still being
/// handled. The wait ends as soon as every message of the session is settled, and no later than the end of the
/// session lock: with auto-renewal enabled the lock is extended, so the session can hold its
/// <c>MaxConcurrentSessions</c> permit for up to <c>MaxAutoLockRenewDuration</c> plus one lock period when the
/// handler is slow or stuck. A lost session lock or a stopping consumer ends the wait earlier.
/// </para>
/// <para>
/// <b>SDK thread-safety:</b> <see cref="ServiceBusReceiver"/> (and its subclass
/// <see cref="ServiceBusSessionReceiver"/>) is documented as thread-safe for concurrent
/// operations. The receive loop and renew task therefore share the same receiver instance safely.
/// </para>
/// </remarks>
internal sealed partial class AzureServiceBusSessionConsumer : IAsyncDisposable, IAzureServiceBusReceiveControl
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

    private readonly AzureServiceBusShutdownBudget _shutdownBudget;
    private readonly TimeSpan _settlementBudget;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _adapterShutdownToken;

    // Completion handles of the per-session receive loops and of the whole per-session tasks. They let
    // EnsureReceiveStoppedAsync wait for in-flight receive calls and StopAsync wait for session teardown.
    private readonly ConcurrentDictionary<long, Task> _receiveLoops = new();
    private readonly ConcurrentDictionary<long, Task> _sessionTasks = new();

    private ulong _deliveryTagCounter;
    private long _taskSequence;

    // Linked to the consume token. Stops ONLY the accept loop and the receive loops: session receivers stay
    // open (lock renewed, settlement possible) until _teardownCts is cancelled by StopAsync.
    private CancellationTokenSource? _receiveCts;
    private CancellationTokenSource? _teardownCts;
    private Task? _outerTask;
    private Task? _receiveStopTask;
    private Task? _stopTask;
    private bool _disposed;

    internal AzureServiceBusSessionConsumer(
        ServiceBusClient client,
        string endpointName,
        AzureServiceBusTransportOptions options,
        AzureServiceBusConsumerRegistry registry,
        string consumerId,
        Channel<InboundMessage> outputChannel,
        FlowControlOptions flowControl,
        ILogger logger,
        TimeSpan? shutdownBudget = null,
        TimeSpan? settlementBudget = null,
        TimeProvider? timeProvider = null,
        CancellationToken adapterShutdownToken = default)
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

        _shutdownBudget = new AzureServiceBusShutdownBudget(
            shutdownBudget ?? AzureServiceBusConsumer.ShutdownDrainBudget);
        _settlementBudget = settlementBudget ?? AzureServiceBusSettlementDrain.Budget;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _adapterShutdownToken = adapterShutdownToken;
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
    /// Starts the outer accept loop on a dedicated long-running task. <paramref name="consumeToken"/> stops
    /// session accepting and receiving; it does NOT close the session receivers — the caller may still
    /// settle in-flight messages on them until <see cref="StopAsync"/> runs.
    /// </summary>
    internal void StartLoop(CancellationToken consumeToken)
    {
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(consumeToken);
        _teardownCts = new CancellationTokenSource();
        CancellationToken receiveToken = _receiveCts.Token;

        // The scheduling token is deliberately not passed: the loop body must always run to its finally.
        _outerTask = Task.Factory.StartNew(
            async () => await RunAcceptLoopAsync(receiveToken).ConfigureAwait(false),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    /// <inheritdoc />
    public bool IsStopRequested => _receiveCts?.IsCancellationRequested ?? false;

    /// <inheritdoc />
    public Task EnsureReceiveStoppedAsync() =>
        SingleFlight(ref _receiveStopTask, StopReceivingAsync);

    /// <summary>
    /// Stops receiving, hands back whatever is still buffered, waits (bounded) for the messages the caller
    /// still settles, then waits for the per-session tasks to tear their receivers down and unregisters this
    /// consumer. Closing a session receiver releases its locks, so buffered messages are only evicted and
    /// disposed — never abandoned one by one. Single-flight and never throws.
    /// </summary>
    internal Task StopAsync() => SingleFlight(ref _stopTask, StopCoreAsync);

    private async Task StopReceivingAsync()
    {
        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);
        }

        await WaitBoundedAsync(_outerTask is null ? [] : [_outerTask]).ConfigureAwait(false);
        await WaitBoundedAsync([.. _receiveLoops.Values]).ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        await EnsureReceiveStoppedAsync().ConfigureAwait(false);

        // Receiving has stopped. Close the shared buffer and hand back what nobody has taken yet; a message that
        // a session task forwards after this point finds the channel closed and is only evicted and disposed.
        _outputChannel.Writer.TryComplete();

        await AzureServiceBusShutdownDrain.DrainAsync(
            _outputChannel.Reader, _registry, _consumerId, _endpointName, abandon: false, _shutdownBudget, _logger)
            .ConfigureAwait(false);

        try
        {
            // The caller may still settle the messages it holds (for example a per-key lane that finishes after
            // the stop), and the session receivers must stay open for that: wait, bounded and not when the
            // adapter itself is being disposed, until nothing is in flight or being settled.
            await AzureServiceBusSettlementDrain.WaitForSettlementsAsync(
                () => _registry.PendingSettlementCount(_consumerId),
                _settlementBudget, _timeProvider, _logger, _consumerId, _endpointName,
                AzureServiceBusSettlementDrain.PollInterval, _adapterShutdownToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_teardownCts is not null)
                {
                    await _teardownCts.CancelAsync().ConfigureAwait(false);
                }

                await WaitBoundedAsync([.. _sessionTasks.Values]).ConfigureAwait(false);
            }
            finally
            {
                _registry.Unregister(_consumerId);
            }
        }
    }

    private async Task WaitBoundedAsync(IReadOnlyList<Task> tasks)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(AzureServiceBusShutdownDrain.ReceiveStopTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — the loops were cancelled.
        }
        catch (TimeoutException)
        {
            LogStopTimedOut(_consumerId, _endpointName);
        }
        catch (Exception ex)
        {
            LogOuterLoopStopError(AzureServiceBusErrorInfo.From(ex));
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

        _receiveCts?.Dispose();
        _teardownCts?.Dispose();
        _shutdownBudget.Dispose();
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
                    LogAcceptSessionError(AzureServiceBusErrorInfo.From(ex));

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
                long taskId = Interlocked.Increment(ref _taskSequence);
                var sessionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _sessionTasks[taskId] = sessionDone.Task;
                try
                {
                    // The scheduling token is deliberately not passed: once a session is accepted the task
                    // must run so it can release the permit and dispose the receiver.
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
                                _sessionTasks.TryRemove(taskId, out _);
                                sessionDone.TrySetResult();
                            }
                        },
                        CancellationToken.None,
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
                    _sessionTasks.TryRemove(taskId, out _);
                    sessionDone.TrySetResult();

                    try
                    {
                        await capturedReceiver.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeEx)
                    {
                        LogSessionReceiverDisposeError(AzureServiceBusErrorInfo.From(disposeEx), sessionId);
                    }

                    LogAcceptSessionError(AzureServiceBusErrorInfo.From(ex));
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
        CancellationToken receiveToken)
    {
        // Per-session bounded channel:
        // SingleWriter = true is legal because this session task is the ONLY writer.
        // FIFO within the session is guaranteed by the sequential single-reader drain below.
        // FullMode is pinned to Wait via BuildSessionChannelOptions — Drop* modes void per-session FIFO.
        Channel<InboundMessage> sessionChannel = Channel.CreateBounded<InboundMessage>(
            BuildSessionChannelOptions(_flowControl.InternalQueueCapacity));

        // Teardown scope of this session — shared by the renew task and the forwarding task. It is cancelled
        // only once the consumer is being torn down (after the caller settled its in-flight messages) or
        // when the session ends on its own, NOT when receiving merely stops.
        using CancellationTokenSource sessionCts =
            CancellationTokenSource.CreateLinkedTokenSource(_teardownCts!.Token);

        CancellationToken sessionToken = sessionCts.Token;

        // Start background renew task BEFORE the receive loop (D-10/PERF-2).
        Task<bool> renewTask = Task.FromResult(false);
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
        bool idleExit = false;

        // Registered before the loop starts so EnsureReceiveStoppedAsync can never miss an in-flight receive.
        long loopId = Interlocked.Increment(ref _taskSequence);
        var loopDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiveLoops[loopId] = loopDone.Task;

        try
        {
            try
            {
                idleExit = await RunSessionReceiveLoopAsync(sessionReceiver, sessionId, sessionChannel, receiveToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _receiveLoops.TryRemove(loopId, out _);
                loopDone.TrySetResult();
            }
        }
        catch (ServiceBusException sbEx) when (sbEx.Reason == ServiceBusFailureReason.SessionLockLost)
        {
            // SessionLockLost — safety net (D-10 proactive renew is primary).
            sessionLockLost = true;
            LogSessionLockLost(_consumerId, _endpointName, sessionId);
        }
        catch (OperationCanceledException)
        {
            // Consume token cancelled — normal shutdown.
        }
        catch (Exception ex)
        {
            LogSessionReceiveError(AzureServiceBusErrorInfo.From(ex), sessionId);
        }
        finally
        {
            // The session went idle, but the caller may still hold messages forwarded from it. Stop writing, keep
            // the receiver (and its lock renewal) alive, and wait for them to be settled, bounded by the session
            // lock. The forwarding task keeps handing over what is already buffered.
            if (idleExit && !sessionLockLost)
            {
                sessionChannel.Writer.TryComplete();
                sessionLockLost |= await WaitForIdleSessionSettlementsAsync(
                    sessionReceiver, sessionId, renewTask, receiveToken).ConfigureAwait(false);
            }

            // Receiving stopped because the consumer is stopping: the caller may still settle the messages it
            // holds from this session, so the receiver (and its lock renewal) must stay alive until StopAsync
            // tears the consumer down. A session that ended on its own (idle, lock lost) is released at once.
            if (receiveToken.IsCancellationRequested && !sessionLockLost)
            {
                await WaitForTeardownAsync().ConfigureAwait(false);
            }

            // Cleanup sequence (D-11/VER-2):
            // 1. Cancel the session CTS (stops the renew task and the forwarding task).
            await sessionCts.CancelAsync().ConfigureAwait(false);

            // 2. Wait for renew task to finish.
            try
            {
                await renewTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogRenewTaskError(AzureServiceBusErrorInfo.From(ex), sessionId); }

            // 3. Complete the per-session channel so the drain task terminates.
            sessionChannel.Writer.TryComplete();

            // 4. Wait for drain task to finish.
            try
            {
                await drainTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogDrainTaskError(AzureServiceBusErrorInfo.From(ex), sessionId); }

            // Messages that never left the per-session channel are not consumed by anyone.
            while (sessionChannel.Reader.TryRead(out InboundMessage? unforwarded))
            {
                unforwarded.Dispose();
            }

            // 5. Bulk-evict all in-flight registry entries for this session (D-11/VER-2).
            _registry.EvictAllForSession(_consumerId, sessionId);

            // 6. Dispose the session receiver (this also releases the locks of its unsettled messages).
            try
            {
                await sessionReceiver.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSessionReceiverDisposeError(AzureServiceBusErrorInfo.From(ex), sessionId);
            }

            LogSessionReleased(_consumerId, _endpointName, sessionId, sessionLockLost);
        }
    }

    /// <summary>
    /// Holds an idle session open until the messages forwarded from it are settled, the session lock ends, the
    /// session lock is lost, or the consumer stops.
    /// </summary>
    /// <returns><see langword="true"/> when the wait was ended by a lost session lock.</returns>
    private async Task<bool> WaitForIdleSessionSettlementsAsync(
        ServiceBusSessionReceiver sessionReceiver,
        string sessionId,
        Task<bool> renewTask,
        CancellationToken receiveToken)
    {
        if (_registry.SessionPendingSettlementCount(_consumerId, sessionId) <= 0)
        {
            return false;
        }

        using CancellationTokenSource waitCts =
            CancellationTokenSource.CreateLinkedTokenSource(receiveToken, _adapterShutdownToken);

        // The unbounded budget leaves the session-lock bound below as the only time limit.
        Task<bool> settled = AzureServiceBusSettlementDrain.WaitForSettlementsAsync(
            () => _registry.SessionPendingSettlementCount(_consumerId, sessionId),
            TimeSpan.MaxValue, _timeProvider, _logger, _consumerId, _endpointName,
            AzureServiceBusSettlementDrain.IdlePollInterval, waitCts.Token);

        Task renewWatch = renewTask;
        bool lockLost = false;

        try
        {
            while (!settled.IsCompleted)
            {
                // Read live: auto-renewal extends the lock while the wait is in progress.
                TimeSpan untilLockEnds = sessionReceiver.SessionLockedUntil - _timeProvider.GetUtcNow();
                if (untilLockEnds <= TimeSpan.Zero)
                {
                    int unsettled = _registry.SessionPendingSettlementCount(_consumerId, sessionId);
                    if (unsettled > 0)
                    {
                        LogIdleSessionLockBoundReached(_consumerId, _endpointName, sessionId, unsettled);
                    }

                    break;
                }

                using var delayCts = new CancellationTokenSource();
                Task finished = await Task.WhenAny(
                    settled, renewWatch, Task.Delay(untilLockEnds, _timeProvider, delayCts.Token)).ConfigureAwait(false);
                await delayCts.CancelAsync().ConfigureAwait(false);

                if (!ReferenceEquals(finished, renewWatch) || ReferenceEquals(renewWatch, settled))
                {
                    continue;
                }

                if (renewTask.IsCompletedSuccessfully && renewTask.Result)
                {
                    lockLost = true;
                    break;
                }

                // The renew loop ended for another reason (renew limit, renewal disabled, error): the lock is
                // not lost, so keep waiting until it ends.
                renewWatch = settled;
            }
        }
        finally
        {
            await waitCts.CancelAsync().ConfigureAwait(false);
            await settled.ConfigureAwait(false);
        }

        return lockLost;
    }

    private async Task WaitForTeardownAsync()
    {
        try
        {
            await Task.Delay(Timeout.Infinite, _teardownCts!.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — StopAsync signalled the teardown.
        }
    }

    // ── Session receive loop ──────────────────────────────────────────────────

    /// <returns><see langword="true"/> when the loop ended because the session went idle (an empty batch).</returns>
    private async Task<bool> RunSessionReceiveLoopAsync(
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
                return false;
            }
            catch (ServiceBusException sbEx) when (sbEx.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                throw; // Re-throw so the outer catch in ProcessSessionAsync handles cleanup.
            }
            catch (Exception ex)
            {
                LogReceiveError(AzureServiceBusErrorInfo.From(ex), sessionId);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                continue;
            }

            if (batch.Count == 0)
            {
                // No messages in the session — idle drain complete; release the session.
                return true;
            }

            for (int i = 0; i < batch.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // Received but never registered: hand the rest of the batch back right away.
                    await AzureServiceBusShutdownDrain.AbandonUnregisteredAsync(
                        sessionReceiver, batch, i, _endpointName, _shutdownBudget, _logger).ConfigureAwait(false);
                    return false;
                }

                InboundMessage message = BuildMessage(batch[i], sessionReceiver, sessionId);

                // Back-pressure: the per-session channel is pinned to Wait and this loop is its only writer, so
                // WaitToWriteAsync blocks while it is full; the branch below runs only after the channel was completed.
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
                catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
                {
                    _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                    message.Dispose();
                    await AzureServiceBusShutdownDrain.AbandonUnregisteredAsync(
                        sessionReceiver, batch, i, _endpointName, _shutdownBudget, _logger).ConfigureAwait(false);
                    return false;
                }
            }
        }

        return false;
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
                // Forward into the shared output channel. Several session drain tasks write here; WriteAsync
                // queues the writers and hands each freed slot to the longest-waiting one, so no writer loses a
                // race for it (it completes synchronously, without allocating, when there is room).
                try
                {
                    await _outputChannel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                    message.Dispose();
                    return;
                }
                catch (ChannelClosedException)
                {
                    // The shared buffer is closed (the consumer is stopping): nobody will consume this
                    // message, so it is only evicted and disposed; its lock is released with the session.
                    _registry.TryEvictMessage(_consumerId, message.DeliveryTag);
                    message.Dispose();
                    LogSessionMessageAfterDrain(_consumerId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    // ── Background renew loop (D-10 / PERF-2 / Opcja A) ─────────────────────

    /// <returns><see langword="true"/> when the loop stopped because the session lock was lost.</returns>
    private async Task<bool> RunRenewLoopAsync(
        ServiceBusSessionReceiver sessionReceiver,
        string sessionId,
        CancellationTokenSource sessionCts,
        CancellationToken cancellationToken)
    {
        DateTimeOffset renewStart = _timeProvider.GetUtcNow();
        TimeSpan maxDuration = _options.MaxAutoLockRenewDuration;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Check total elapsed renew time.
                if (maxDuration > TimeSpan.Zero &&
                    _timeProvider.GetUtcNow() - renewStart >= maxDuration)
                {
                    LogRenewDurationExceeded(_consumerId, _endpointName, sessionId);
                    break;
                }

                // Compute sleep interval from the live SessionLockedUntil value (D-10 PERF-2).
                // MUST use SessionLockedUntil, NOT MaxAutoLockRenewDuration, as the sleep interval —
                // confusing these would cause the lock to expire before the first renew.
                DateTimeOffset lockedUntil = sessionReceiver.SessionLockedUntil;
                TimeSpan remaining = lockedUntil - _timeProvider.GetUtcNow() - RenewSafetyMargin;
                TimeSpan sleepInterval = remaining / 2;

                if (sleepInterval < RenewMinFloor)
                {
                    sleepInterval = RenewMinFloor;
                }

                try
                {
                    await Task.Delay(sleepInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
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
                    return true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogRenewError(AzureServiceBusErrorInfo.From(ex), sessionId);
                    // Continue retrying — transient error.
                }
            }
        }
        catch (OperationCanceledException) { }

        return false;
    }

    // ── Session channel factory ───────────────────────────────────────────────

    /// <summary>
    /// Builds <see cref="BoundedChannelOptions"/> for a per-session channel.
    /// </summary>
    /// <param name="capacity">Channel capacity from <see cref="FlowControlOptions.InternalQueueCapacity"/>.</param>
    /// <returns>Options with <see cref="BoundedChannelFullMode.Wait"/> pinned unconditionally.</returns>
    /// <remarks>
    /// Drop* modes (DropWrite / DropOldest / DropNewest) void the per-session FIFO guarantee
    /// a mid-session message could be silently dropped while later messages
    /// still enqueue, creating a hole in the ordered sequence within a SessionId. The session
    /// path therefore always uses Wait, regardless of the caller's FlowControlOptions.FullMode.
    /// </remarks>
    internal static BoundedChannelOptions BuildSessionChannelOptions(int capacity) =>
        new(capacity)
        {
            // Drop* modes void per-session FIFO, so the session path pins Wait.
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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus session consumer {ConsumerId} on queue '{QueueName}': idle session '{SessionId}' released with {Count} unsettled message(s) because its session lock ended.")]
    private partial void LogIdleSessionLockBoundReached(string consumerId, string queueName, string sessionId, int count);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Azure Service Bus session consumer {ConsumerId}: session lock renewed for session '{SessionId}' on queue '{QueueName}'. LockedUntil={LockedUntil}.")]
    private partial void LogSessionLockRenewed(string consumerId, string queueName, string sessionId, DateTimeOffset lockedUntil);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus session consumer {ConsumerId}: MaxAutoLockRenewDuration exceeded for session '{SessionId}' on queue '{QueueName}'. Background renew stopped.")]
    private partial void LogRenewDurationExceeded(string consumerId, string queueName, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error accepting session on queue. Will back off and retry ({Error}).")]
    private partial void LogAcceptSessionError(AzureServiceBusErrorInfo error);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error receiving messages for session '{SessionId}'. Will retry after back-off ({Error}).")]
    private partial void LogReceiveError(AzureServiceBusErrorInfo error, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: unexpected error in session '{SessionId}' receive loop ({Error}).")]
    private partial void LogSessionReceiveError(AzureServiceBusErrorInfo error, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error in renew task for session '{SessionId}' ({Error}).")]
    private partial void LogRenewError(AzureServiceBusErrorInfo error, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error in renew task for session '{SessionId}' during shutdown ({Error}).")]
    private partial void LogRenewTaskError(AzureServiceBusErrorInfo error, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error in drain task for session '{SessionId}' during shutdown ({Error}).")]
    private partial void LogDrainTaskError(AzureServiceBusErrorInfo error, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer: error disposing session receiver for session '{SessionId}' ({Error}).")]
    private partial void LogSessionReceiverDisposeError(AzureServiceBusErrorInfo error, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure Service Bus session consumer loop terminated with an unexpected error ({Error}).")]
    private partial void LogOuterLoopStopError(AzureServiceBusErrorInfo error);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus session consumer {ConsumerId} on queue '{QueueName}': message DeliveryTag={DeliveryTag} could not be buffered because the per-session channel was closed; registry entry evicted. SessionId='{SessionId}'.")]
    private partial void LogMessageDropped(string consumerId, string queueName, ulong deliveryTag, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Azure Service Bus session consumer {ConsumerId}: a session message arrived after the shared buffer was closed; it was evicted and its lock is released with the session.")]
    private partial void LogSessionMessageAfterDrain(string consumerId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Azure Service Bus session consumer {ConsumerId} on queue '{QueueName}': session tasks did not stop in time; continuing shutdown.")]
    private partial void LogStopTimedOut(string consumerId, string queueName);
}
