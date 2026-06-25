using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Bus;

/// <summary>
/// Per-lane poison / anti-starvation contract for the per-key ordered dispatch path (R8.12,
/// ADR-026 §7). Owned by each <c>OrderedDispatchStage.Lane</c>; state is O(laneCount), not
/// O(messages) (ADR-003 — no per-message allocation).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Default OFF invariant (ADR-026 §MUSI):</strong> when <c>MaxDeliveryAttempts == 0</c>
/// (the default), this contract is disabled and the lane behaves exactly as in R8.7 — NACK on
/// handler exception, unconditional release, at-least-once, no counting, no parking. The
/// sequential path (per-key ordering OFF) is not touched at all.
/// </para>
/// <para>
/// <strong>C3 — release ONLY after durable ack:</strong> the lane MUST NOT read the next
/// <c>WorkItem</c> until <see cref="DurableSettlementResult.IsDurablyConfirmed"/> is
/// <see langword="true"/>. When the durable-park attempt fails, the lane stays on the head and
/// retries park with a bounded loop (reuse <c>RetryCount</c>) + a forced non-zero delay floor
/// (to avoid CPU-spin when <c>RetryInterval == 0</c>). After the retry bound is exhausted the
/// head is kept (ordering preserved over skipping; ADR-026 §7 "never block forever" trade-off
/// is honoured by bounding retries).
/// </para>
/// <para>
/// <strong>Security (S1/S2):</strong> no ordering-key value is ever logged. Gap-logs and
/// failed-settle logs use <see cref="OrderingKeyDiagnostics.ToOpaqueToken"/> only. The
/// <see cref="DurableSettlementResult.FailureReason"/> is a constant category string — logged
/// as-is without embedding key/body/routing-key data.
/// </para>
/// </remarks>
internal sealed partial class PoisonContract
{
    // Minimum delay between park-retry attempts — prevents CPU-spin when RetryInterval == 0
    // (OQ-5: IntervalRetryPolicy.GetDelay returns the configured interval, which may be zero).
    private static readonly TimeSpan MinParkRetryDelay = TimeSpan.FromMilliseconds(50);

    // Delay applied by the outer C3 loop in Lane.RunAsync between HandleOutcomeAsync calls
    // when all ParkDurablyAsync attempts are exhausted. Prevents hot-spin when the park
    // endpoint is persistently down and the mock/real impl returns synchronously.
    internal static readonly TimeSpan C3RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly int _maxDeliveryAttempts;
    private readonly IDurableParkSettlement? _durablePark;
    private readonly string? _deadLetterExchange;
    private readonly string _deadLetterRoutingKey; // never null: falls back to endpointName
    private readonly string _endpointName;
    private readonly int _parkRetryCount;      // >= 1
    private readonly TimeSpan _parkRetryInterval;
    private readonly ILogger _logger;

    // Per-head tracking — reset when head changes (MessageId changes or action is not Nack).
    // These are per-lane state: allocation is O(laneCount), not O(messages).
    private string? _currentHeadMessageId; // null = no tracked head yet
    private int _currentHeadAttempts;      // number of Nack deliveries for the current head

    internal PoisonContract(
        EndpointBinding binding,
        ITransportAdapter adapter,
        ILogger logger)
    {
        _maxDeliveryAttempts = binding.Ordering?.MaxDeliveryAttempts ?? 0;
        _durablePark = adapter as IDurableParkSettlement;
        _deadLetterExchange = binding.DeadLetterExchange;
        // Routing key: use the configured DLX routing key if set; otherwise fall back to the
        // queue name (matches RabbitMQ DLX default semantics: original routing key preserved).
        _deadLetterRoutingKey = binding.DeadLetterRoutingKey ?? binding.EndpointName;
        _endpointName = binding.EndpointName;
        _logger = logger;

        // Park-retry bound: reuse endpoint RetryCount (OQ-5). At least 1 attempt.
        _parkRetryCount = binding.RetryCount < 1 ? 1 : binding.RetryCount;
        _parkRetryInterval = binding.RetryInterval;
    }

    /// <summary>
    /// Processes the settlement outcome of a dispatch attempt for the ordered path.
    /// </summary>
    /// <param name="action">The settlement action determined by <c>ProcessMessageAsync</c>.</param>
    /// <param name="message">The inbound message that was dispatched.</param>
    /// <param name="adapter">The transport adapter for settling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the lane SHOULD advance to the next WorkItem (release);
    /// <see langword="false"/> when the lane MUST NOT advance (C3 — head not durably resolved).
    /// </returns>
    internal async ValueTask<bool> HandleOutcomeAsync(
        SettlementAction action,
        InboundMessage message,
        ITransportAdapter adapter,
        CancellationToken cancellationToken)
    {
        // Contract DISABLED (default): behave exactly as R8.7 — settle and release unconditionally.
        if (_maxDeliveryAttempts == 0)
        {
            await SettleAndForgetAsync(adapter, action, message, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        // Non-Nack outcomes (Ack / Reject / Requeue) settle normally and reset the counter.
        if (action != SettlementAction.Nack)
        {
            ResetHeadTracking();
            await SettleAndForgetAsync(adapter, action, message, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        // --- NACK path: count attempts per head ---

        // OQ-4: empty/unparseable MessageId → head is untrackable → behave as MaxDeliveryAttempts==0.
        if (string.IsNullOrEmpty(message.MessageId))
        {
            await SettleAndForgetAsync(adapter, SettlementAction.Nack, message, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        // New head (MessageId changed) → reset counter.
        if (message.MessageId != _currentHeadMessageId)
        {
            _currentHeadMessageId = message.MessageId;
            _currentHeadAttempts = 0;
        }

        _currentHeadAttempts++;

        // Under threshold → redeliver (NACK, broker re-queues); keep head, preserve ordering.
        if (_currentHeadAttempts < _maxDeliveryAttempts)
        {
            await SettleAndForgetAsync(adapter, SettlementAction.Nack, message, cancellationToken)
                .ConfigureAwait(false);
            return true; // lane advances; broker will re-deliver the same head next
        }

        // --- PARK threshold reached ---
        return await ParkHeadAsync(message, adapter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to durably park the head message. Returns <see langword="true"/> when the head
    /// is permanently resolved (lane may advance); <see langword="false"/> when C3 prevents
    /// release (lane must NOT advance).
    /// </summary>
    private async ValueTask<bool> ParkHeadAsync(
        InboundMessage message,
        ITransportAdapter adapter,
        CancellationToken cancellationToken)
    {
        string token = OrderingKeyDiagnostics.ToOpaqueToken(message.MessageId);

        // Branch 1: transport supports durable park AND DLX is configured.
        if (_durablePark is not null && !string.IsNullOrEmpty(_deadLetterExchange))
        {
            return await ParkDurablyAsync(message, token, cancellationToken).ConfigureAwait(false);
        }

        // Branch 2: no IDurableParkSettlement but DLX is configured — narrower guarantee (option b).
        // Release happens after SettleAsync(Nack) without durable confirmation.
        // DELIBERATE SCOPE NARROWING: the full C3 guarantee requires IDurableParkSettlement on the
        // transport. Without it, the message is routed via x-dead-letter-exchange by the broker on
        // NACK, but this code does not wait for a publisher-confirm before releasing the lane.
        // Documented as option (b) from R8.3.
        if (!string.IsNullOrEmpty(_deadLetterExchange))
        {
            await SettleAndForgetAsync(adapter, SettlementAction.Nack, message, cancellationToken)
                .ConfigureAwait(false);
            LogPoisonGapNarrowDlx(_endpointName, token);
            ResetHeadTracking();
            return true;
        }

        // Branch 3: no DLX at all — message would be lost; log and reject per ADR-026 §7
        // ("no block-forever path" — we must release rather than stall permanently).
        await SettleAndForgetAsync(adapter, SettlementAction.Reject, message, cancellationToken)
            .ConfigureAwait(false);
        LogMessageLostNoDlx(_endpointName, message.MessageId);
        ResetHeadTracking();
        return true;
    }

    /// <summary>
    /// Attempts durable park with a bounded retry loop (C3). Returns <see langword="true"/>
    /// when <see cref="DurableSettlementResult.IsDurablyConfirmed"/>; <see langword="false"/>
    /// when all retries are exhausted (lane MUST NOT advance — ordering preserved).
    /// </summary>
    private async ValueTask<bool> ParkDurablyAsync(
        InboundMessage message,
        string opaqueToken,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < _parkRetryCount; attempt++)
        {
            if (attempt > 0)
            {
                // Forced non-zero delay floor (OQ-5 / PERF-1): IntervalRetryPolicy can return zero,
                // which would spin the CPU on a persistently-down broker. Clamp to the floor.
                TimeSpan delay = _parkRetryInterval < MinParkRetryDelay
                    ? MinParkRetryDelay
                    : _parkRetryInterval;

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            DurableSettlementResult result = await _durablePark!.ParkHeadDurablyAsync(
                message, _deadLetterExchange!, _deadLetterRoutingKey, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsDurablyConfirmed)
            {
                LogPoisonGapDurable(_endpointName, opaqueToken);
                ResetHeadTracking();
                return true;
            }

            // C3: do NOT release. Log failed-settle with constant category, no key value.
            LogPoisonParkFailed(_endpointName, result.FailureReason ?? "unknown");
        }

        // All retries exhausted — head stays (ordering > skipping). Log error, return false (C3).
        LogPoisonParkExhausted(_endpointName, opaqueToken);
        return false;
    }

    private void ResetHeadTracking()
    {
        _currentHeadMessageId = null;
        _currentHeadAttempts = 0;
    }

    private static async Task SettleAndForgetAsync(
        ITransportAdapter adapter,
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            CancellationToken settleCt = action == SettlementAction.Requeue
                ? CancellationToken.None
                : cancellationToken;
            await adapter.SettleAsync(action, message, settleCt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Settlement errors on the ordered path are non-fatal: the lane continues. The caller
            // (Lane.RunAsync) already wraps the whole WorkItem in try/finally for credit release.
            _ = ex;
        }
    }

    // ── LoggerMessage declarations ────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Poison head parked durably on endpoint '{EndpointName}' — " +
                  "gap correlation token: {OpaqueToken}.")]
    private partial void LogPoisonGapDurable(string endpointName, string opaqueToken);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Poison head parked via DLX (narrow guarantee, no durable ack) on endpoint " +
                  "'{EndpointName}' — gap correlation token: {OpaqueToken}.")]
    private partial void LogPoisonGapNarrowDlx(string endpointName, string opaqueToken);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Durable park settlement failed on endpoint '{EndpointName}' — " +
                  "failure category: {FailureCategory}. Head retained (C3 — ordering preserved).")]
    private partial void LogPoisonParkFailed(string endpointName, string failureCategory);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Durable park retry bound exhausted on endpoint '{EndpointName}' — " +
                  "gap correlation token: {OpaqueToken}. Head retained (C3 — ordering preserved).")]
    private partial void LogPoisonParkExhausted(string endpointName, string opaqueToken);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Message {MessageId} on endpoint '{EndpointName}' will be permanently lost — " +
                  "no dead-letter exchange configured. Head released (ADR-026 §7 no-block-forever).")]
    private partial void LogMessageLostNoDlx(string endpointName, string messageId);
}
