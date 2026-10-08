using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Pipeline;
using BareWire.Pipeline.Retry;
using Microsoft.Extensions.Logging;

namespace BareWire.Pipeline;

internal sealed class RetryMiddleware : IMessageMiddleware
{
    private readonly RetryPolicy _policy;
    private readonly ILogger<RetryMiddleware> _logger;
    private readonly IBareWireInstrumentation _instrumentation;
    private readonly string _messageTypeTag;

    internal RetryMiddleware(
        RetryPolicy policy,
        ILogger<RetryMiddleware> logger,
        IBareWireInstrumentation instrumentation,
        string messageTypeTag)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instrumentation = instrumentation ?? throw new ArgumentNullException(nameof(instrumentation));
        _messageTypeTag = messageTypeTag ?? throw new ArgumentNullException(nameof(messageTypeTag));
    }

    public async Task InvokeAsync(MessageContext context, NextMiddleware nextMiddleware)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nextMiddleware);

        int attempt = 0;

        while (true)
        {
            try
            {
                await nextMiddleware(context).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Never retry cancellation — propagate immediately
                throw;
            }
            catch (Exception ex) when (!IsHandledByConsumerRetry(context) && _policy.ShouldRetry(ex, attempt))
            {
                TimeSpan delay = _policy.GetDelay(attempt);
                RetryMiddlewareLogMessages.RetryingMessage(
                    _logger, context.MessageId, attempt + 1, _policy.MaxRetries, delay, ex.GetType().Name);

                // Retry-branch only (never on the 0-B/op success path); TagList is a struct — no heap alloc.
                _instrumentation.RecordRetryAttempt(context.EndpointName, _messageTypeTag, ex.GetType().Name);

                await _policy.DelayAsync(attempt, context.CancellationToken).ConfigureAwait(false);

                // Let outer middleware (e.g. the transactional outbox) discard side effects buffered by the
                // failed attempt. HasItems keeps this allocation-free when nothing registered a callback.
                if (context.HasItems
                    && context.Items.TryGetValue(WellKnownItemKeys.RetryAttemptStarting, out object? callback)
                    && callback is Action reset)
                {
                    reset();
                }

                attempt++;
            }
            catch (Exception ex)
            {
                // A consumer-level policy already logged its own "retries exhausted" entry; do not log a second one.
                if (!IsHandledByConsumerRetry(context))
                {
                    RetryMiddlewareLogMessages.RetriesExhausted(
                        _logger, context.MessageId, attempt, ex.GetType().Name);
                }

                throw;
            }
        }
    }

    private static bool IsHandledByConsumerRetry(MessageContext context) =>
        context.HasItems && context.Items.ContainsKey(InternalItemKeys.ConsumerRetryExhausted);
}

internal static partial class RetryMiddlewareLogMessages
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Retrying message {MessageId} (attempt {Attempt}/{MaxRetries}) after {Delay} due to {ExceptionType}")]
    internal static partial void RetryingMessage(
        ILogger logger,
        Guid messageId,
        int attempt,
        int maxRetries,
        TimeSpan delay,
        string exceptionType);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Message {MessageId} failed after {AttemptCount} attempt(s) with {ExceptionType}; retries exhausted")]
    internal static partial void RetriesExhausted(
        ILogger logger,
        Guid messageId,
        int attemptCount,
        string exceptionType);
}
