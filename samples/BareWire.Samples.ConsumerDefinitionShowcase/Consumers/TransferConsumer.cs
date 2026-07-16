using BareWire.Abstractions;
using BareWire.Samples.ConsumerDefinitionShowcase.Messages;
using BareWire.Samples.ConsumerDefinitionShowcase.Services;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.ConsumerDefinitionShowcase.Consumers;

/// <summary>
/// Handles <see cref="TransferInitiated"/> deliveries dispatched via the routing-key patterns
/// declared on <c>TransferConsumerDefinition</c> (<c>"transfer.eu.*"</c> / <c>"transfer.eu.priority"</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately fails to prove the retry policy fires.</strong> The first
/// <see cref="RequiredAttempts"/> <c>- 1</c> attempts throw a transient exception; only once
/// <see cref="TransferObservations.NextAttempt"/> reaches <see cref="RequiredAttempts"/> does this
/// consumer succeed and record its observation (carrying the attempt count). Because the retry
/// policy — not this class — is what re-delivers the message, a recorded observation with
/// <c>Attempts &gt; 1</c> is proof that <c>TransferConsumerDefinition</c>'s
/// <c>consumer.Retry(r =&gt; r.Exponential(...))</c> policy actually re-delivered the message.
/// </para>
/// <para>
/// Routing-key dispatch and the retry policy are both declared on
/// <see cref="Definitions.TransferConsumerDefinition"/> — this class stays free of that
/// configuration and only implements the consume behavior.
/// </para>
/// </remarks>
internal sealed partial class TransferConsumer(
    TransferObservations observations,
    ILogger<TransferConsumer> logger) : IConsumer<TransferInitiated>
{
    /// <summary>
    /// Number of delivery attempts required before this consumer succeeds. Chosen to be below the
    /// definition's configured retry count (4) so the final attempt is always covered by the policy.
    /// </summary>
    private const int RequiredAttempts = 3;

    public Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        int attempt = observations.NextAttempt(context.Message.TransferId);

        if (attempt < RequiredAttempts)
        {
            LogTransientFailure(logger, context.Message.TransferId, attempt);

            throw new InvalidOperationException(
                $"TransferConsumer: simulated transient failure on attempt {attempt} for transfer " +
                $"{context.Message.TransferId} — the definition's retry policy should re-deliver this message.");
        }

        // TryGetValue avoids KeyNotFoundException when the header is absent.
        context.Headers.TryGetValue("BW-RoutingKey", out string? routingKey);

        observations.Record(new TransferObservation(
            RunId: context.Message.RunId,
            RoutingKey: routingKey ?? string.Empty,
            ConsumerName: nameof(TransferConsumer),
            Attempts: attempt,
            TransferId: context.Message.TransferId));

        LogDispatched(logger, context.Message.TransferId, attempt);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "TransferConsumer: transient failure on attempt {Attempt} for transfer {TransferId}")]
    private static partial void LogTransientFailure(ILogger logger, string transferId, int attempt);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "TransferConsumer: dispatched transfer {TransferId} after {Attempts} attempt(s)")]
    private static partial void LogDispatched(ILogger logger, string transferId, int attempts);
}
