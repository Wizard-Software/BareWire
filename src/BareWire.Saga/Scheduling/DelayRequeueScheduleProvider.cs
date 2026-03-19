using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Saga.Scheduling;

internal sealed partial class DelayRequeueScheduleProvider : IScheduleProvider
{
    private readonly ITransportAdapter _transport;
    private readonly ILogger<DelayRequeueScheduleProvider> _logger;

    internal DelayRequeueScheduleProvider(
        ITransportAdapter transport,
        ILogger<DelayRequeueScheduleProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _logger = logger;
    }

    public async Task ScheduleAsync<T>(
        T message,
        TimeSpan delay,
        string destinationQueue,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destinationQueue);

        var ttlMs = (long)delay.TotalMilliseconds;
        var delayQueueName = $"barewire.delay.{ttlMs}.{destinationQueue}";

        // A separate delay queue is created per unique TTL value to avoid the RabbitMQ
        // head-of-queue expiration gotcha: per-message TTL only expires messages at the
        // head of the queue, so messages with shorter TTL behind longer-TTL messages would
        // not expire on time. Using queue-level x-message-ttl on a dedicated per-TTL queue
        // guarantees correct expiration semantics. See plan section 9 (TTL gotcha decision).
        LogScheduling(_logger, typeof(T).Name, ttlMs, delayQueueName, destinationQueue);

        // TODO: When ITransportAdapter supports queue declaration with arguments,
        // declare the delay queue with x-message-ttl={ttlMs} and
        // x-dead-letter-exchange + x-dead-letter-routing-key pointing to destinationQueue,
        // then publish the serialized message to that delay queue.
        // The broker will automatically dead-letter the message to destinationQueue after TTL.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task CancelAsync<T>(Guid correlationId, CancellationToken cancellationToken = default)
        where T : class
    {
        // RabbitMQ does not support selective message deletion from a queue.
        // Best-effort cancellation: log a warning so the consumer knows to check
        // whether the scheduled timeout is still relevant when it is eventually delivered.
        LogCancelNotSupported(_logger, typeof(T).Name, correlationId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Scheduling message of type {MessageType} with delay {DelayMs}ms via queue {DelayQueue} -> {DestinationQueue}")]
    private static partial void LogScheduling(
        ILogger logger, string messageType, long delayMs, string delayQueue, string destinationQueue);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Cancel timeout requested for {MessageType} (correlationId={CorrelationId}) but RabbitMQ " +
                  "does not support selective message deletion. The timeout message will still be delivered " +
                  "and must be discarded by the consumer if the saga no longer expects it.")]
    private static partial void LogCancelNotSupported(ILogger logger, string messageType, Guid correlationId);
}
