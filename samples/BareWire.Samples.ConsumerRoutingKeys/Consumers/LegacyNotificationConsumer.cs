using BareWire.Abstractions;
using BareWire.Samples.ConsumerRoutingKeys.Messages;
using BareWire.Samples.ConsumerRoutingKeys.Services;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.ConsumerRoutingKeys.Consumers;

/// <summary>
/// Handles <see cref="LegacyNotification"/> messages whose delivery routing key matches
/// <c>legacy.#</c> (any routing key starting with "legacy.") and that carry no
/// <c>BW-MessageType</c> header (foreign / raw JSON from a non-BareWire producer).
/// </summary>
/// <remarks>
/// <para>
/// <strong>AcceptUntyped() is a mandatory explicit opt-in.</strong> Without it, this consumer
/// is never a candidate for type-less deliveries, even if the routing key matches. This is the
/// secure-by-default behavior: typed consumers are never silently exposed to untrusted foreign JSON.
/// </para>
/// <para>
/// <strong>SECURITY CAVEAT:</strong> Production endpoints using <c>AcceptUntyped()</c> must
/// additionally enforce broker-level publish ACLs (e.g. RabbitMQ vhost permissions) AND apply
/// schema validation with a payload-size limit before processing foreign input. This sample omits
/// those guards because it is self-published and has a zero blast radius; they are not defaults.
/// </para>
/// </remarks>
internal sealed partial class LegacyNotificationConsumer(
    RoutingObservations observations,
    ILogger<LegacyNotificationConsumer> logger) : IConsumer<LegacyNotification>
{
    public Task ConsumeAsync(ConsumeContext<LegacyNotification> context)
    {
        context.Headers.TryGetValue("BW-RoutingKey", out string? routingKey);

        observations.Record(
            runId: context.Message.RunId,
            routingKey: routingKey ?? string.Empty,
            consumerName: nameof(LegacyNotificationConsumer),
            messageType: nameof(LegacyNotification),
            typeLess: true,
            echo: context.Message.Detail);

        LogDispatched(logger, context.Message.NotificationId, context.Message.Source);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "LegacyNotificationConsumer: dispatched notification {NotificationId} from source {Source}")]
    private static partial void LogDispatched(ILogger logger, string notificationId, string source);
}
