using BareWire.Abstractions;
using BareWire.Samples.ConsumerRoutingKeys.Messages;
using BareWire.Samples.ConsumerRoutingKeys.Services;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.ConsumerRoutingKeys.Consumers;

/// <summary>
/// Handles <see cref="TransferInitiated"/> messages whose delivery routing key is exactly
/// <c>transfer.eu.priority</c> (no wildcards).
/// </summary>
/// <remarks>
/// Most-specific-wins: an exact routing-key pattern (no <c>*</c> or <c>#</c>) is always more
/// specific than a wildcard pattern. A delivery on <c>transfer.eu.priority</c> matches both this
/// consumer's exact pattern and <see cref="RegionTransferConsumer"/>'s <c>transfer.eu.*</c>, but
/// the exact match wins deterministically — this consumer is selected.
/// </remarks>
internal sealed partial class PriorityTransferConsumer(
    RoutingObservations observations,
    ILogger<PriorityTransferConsumer> logger) : IConsumer<TransferInitiated>
{
    public Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        context.Headers.TryGetValue("BW-RoutingKey", out string? routingKey);

        observations.Record(
            runId: context.Message.RunId,
            routingKey: routingKey ?? string.Empty,
            consumerName: nameof(PriorityTransferConsumer),
            messageType: nameof(TransferInitiated),
            typeLess: false,
            echo: context.Message.TransferId);

        LogDispatched(logger, context.Message.TransferId, context.Message.Kind);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "PriorityTransferConsumer: dispatched transfer {TransferId} of kind {Kind}")]
    private static partial void LogDispatched(ILogger logger, string transferId, string kind);
}
