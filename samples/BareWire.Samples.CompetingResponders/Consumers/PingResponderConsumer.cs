using BareWire.Abstractions;
using BareWire.Samples.CompetingResponders.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.CompetingResponders.Consumers;

/// <summary>
/// Holds the identity of this responder instance (singleton, computed once at startup).
/// </summary>
internal sealed record ResponderIdentity(string Id);

/// <summary>
/// Competing responder: every replica receives a copy of each <see cref="PingRequest"/> via
/// the per-type fanout exchange, processes it fully, and calls <see cref="ConsumeContext{T}.RespondAsync"/>
/// to route the answer back through the ReplyTo path. The framework delivers the first arriving
/// response to the <see cref="IRequestClient{T}"/> caller and silently drops the rest (first-in-wins).
/// </summary>
internal sealed partial class PingResponderConsumer(ResponderIdentity identity, ILogger<PingResponderConsumer> logger)
    : IConsumer<PingRequest>
{
    public async Task ConsumeAsync(ConsumeContext<PingRequest> context)
    {
        LogHandling(logger, identity.Id, context.Message.Payload);

        await context.RespondAsync(
            new PingResponse(context.Message.Payload, identity.Id),
            context.CancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Responder {ResponderId} handling request {Payload}")]
    private static partial void LogHandling(ILogger logger, string responderId, string payload);
}
