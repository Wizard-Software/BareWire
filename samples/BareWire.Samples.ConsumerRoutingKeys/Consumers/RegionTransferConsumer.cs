using BareWire.Abstractions;
using BareWire.Samples.ConsumerRoutingKeys.Messages;
using BareWire.Samples.ConsumerRoutingKeys.Services;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.ConsumerRoutingKeys.Consumers;

/// <summary>
/// Handles <see cref="TransferInitiated"/> messages whose delivery routing key matches
/// the pattern <c>transfer.eu.*</c> (EU region, any transfer kind).
/// </summary>
/// <remarks>
/// Most-specific-wins: a delivery on routing key <c>transfer.eu.priority</c> matches both
/// this pattern and <c>PriorityTransferConsumer</c>'s exact pattern. The exact pattern wins,
/// so priority deliveries are dispatched to <see cref="PriorityTransferConsumer"/> only.
/// Standard EU deliveries (<c>transfer.eu.standard</c>) match this wildcard pattern only.
/// </remarks>
internal sealed partial class RegionTransferConsumer(
    RoutingObservations observations,
    ILogger<RegionTransferConsumer> logger) : IConsumer<TransferInitiated>
{
    public Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        // TryGetValue avoids KeyNotFoundException when the header is absent.
        // BW-RoutingKey is always present for RabbitMQ deliveries, but TryGetValue
        // is the defensive idiom consistent with how the transport core reads headers.
        context.Headers.TryGetValue("BW-RoutingKey", out string? routingKey);

        observations.Record(
            runId: context.Message.RunId,
            routingKey: routingKey ?? string.Empty,
            consumerName: nameof(RegionTransferConsumer),
            messageType: nameof(TransferInitiated),
            typeLess: false,
            echo: context.Message.TransferId);

        LogDispatched(logger, context.Message.TransferId, context.Message.Region);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "RegionTransferConsumer: dispatched transfer {TransferId} from region {Region}")]
    private static partial void LogDispatched(ILogger logger, string transferId, string region);
}
