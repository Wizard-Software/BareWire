using System.Buffers;
using System.Collections.Concurrent;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Saga.Scheduling;

internal sealed partial class DelayRequeueScheduleProvider : IScheduleProvider
{
    private readonly ITransportAdapter _transport;
    private readonly IMessageSerializer _serializer;
    private readonly ILogger<DelayRequeueScheduleProvider> _logger;
    private readonly ConcurrentDictionary<string, bool> _declaredQueues = new();

    internal DelayRequeueScheduleProvider(
        ITransportAdapter transport,
        IMessageSerializer serializer,
        ILogger<DelayRequeueScheduleProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _serializer = serializer;
        _logger = logger;
    }

    public async Task ScheduleAsync<T>(
        T message,
        TimeSpan delay,
        string destinationQueue,
        Guid correlationId,
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

        // Deploy delay queue only if not yet declared in this process lifetime.
        // ConcurrentDictionary.TryAdd returns true only for the first caller — subsequent
        // callers skip the DeployTopologyAsync call, avoiding redundant broker round-trips.
        if (_declaredQueues.TryAdd(delayQueueName, true))
        {
            var topology = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(
                        Name: delayQueueName,
                        Durable: true,
                        AutoDelete: false,
                        Arguments: new Dictionary<string, object>
                        {
                            ["x-message-ttl"] = ttlMs,
                            ["x-dead-letter-exchange"] = "",
                            ["x-dead-letter-routing-key"] = destinationQueue
                        })
                ]
            };

            await _transport.DeployTopologyAsync(topology, cancellationToken).ConfigureAwait(false);
        }

        // Serialize message. This is not a hot path — ArrayBufferWriter<byte> allocation is acceptable.
        var writer = new ArrayBufferWriter<byte>();
        _serializer.Serialize(message, writer);
        ReadOnlyMemory<byte> body = writer.WrittenMemory.ToArray();

        var outbound = new OutboundMessage(
            routingKey: delayQueueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = "",
                ["BW-MessageType"] = typeof(T).Name,
                ["message-id"] = Guid.NewGuid().ToString()
            },
            body: body,
            contentType: _serializer.ContentType);

        IReadOnlyList<SendResult> results = await _transport.SendBatchAsync([outbound], cancellationToken)
            .ConfigureAwait(false);

        if (results.Count > 0 && !results[0].IsConfirmed)
        {
            throw new InvalidOperationException(
                $"Broker did not confirm scheduled message for queue '{delayQueueName}'. " +
                "The timeout message may not be delivered.");
        }
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
