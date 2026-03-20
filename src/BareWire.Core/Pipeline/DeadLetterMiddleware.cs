using BareWire.Abstractions.Pipeline;
using Microsoft.Extensions.Logging;

namespace BareWire.Core.Pipeline;

internal sealed class DeadLetterMiddleware : IMessageMiddleware
{
    private readonly Func<MessageContext, Exception, Task> _onDeadLetter;
    private readonly ILogger<DeadLetterMiddleware> _logger;

    internal DeadLetterMiddleware(
        Func<MessageContext, Exception, Task> onDeadLetter,
        ILogger<DeadLetterMiddleware> logger)
    {
        _onDeadLetter = onDeadLetter ?? throw new ArgumentNullException(nameof(onDeadLetter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(MessageContext context, NextMiddleware nextMiddleware)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nextMiddleware);

        try
        {
            await nextMiddleware(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation — do not dead-letter on shutdown
            throw;
        }
        catch (Exception ex)
        {
            DeadLetterMiddlewareLogMessages.RoutingToDeadLetter(
                _logger, context.MessageId, ex.GetType().Name);

            await _onDeadLetter(context, ex).ConfigureAwait(false);

            // Re-throw so ReceiveEndpointRunner can NACK — broker routes via x-dead-letter-exchange (DLX).
            throw;
        }
    }
}

internal static partial class DeadLetterMiddlewareLogMessages
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Message {MessageId} routed to dead-letter queue due to {ExceptionType}")]
    internal static partial void RoutingToDeadLetter(
        ILogger logger,
        Guid messageId,
        string exceptionType);
}
