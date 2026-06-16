using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Kafka;

/// <summary>
/// Consumer side of the Kafka transport adapter.
/// Implements <see cref="ITransportAdapter.ConsumeAsync"/> and
/// <see cref="ITransportAdapter.SettleAsync"/> (R1.2).
/// </summary>
internal sealed partial class KafkaTransportAdapter
{
    private readonly KafkaConsumerRegistry _consumerRegistry = new();

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Builds a dedicated <see cref="IConsumer{TKey,TValue}"/> per call, subscribes to
    /// <paramref name="endpointName"/> as a Kafka topic, starts the polling loop on a
    /// long-running thread (D2), and exposes messages as an <see cref="IAsyncEnumerable{T}"/>
    /// via a bounded <see cref="Channel{T}"/> (ADR-004/D7).
    /// </para>
    /// <para>
    /// <b>Full consume/commit/rebalance behaviour</b> is validated by integration tests (R1.5).
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Validate consumer-specific options (GroupId required; producer Validate() already ran in ctor).
        _options.ValidateConsumer();

        var inboundChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                FullMode = flowControl.FullMode,
                SingleWriter = true,
                SingleReader = false,
            });

        string consumerId = Guid.NewGuid().ToString("N");

        IConsumer<byte[], byte[]> nativeConsumer = BuildNativeConsumer(consumerId);

        var kafkaConsumer = new KafkaConsumer(
            consumer: nativeConsumer,
            channel: inboundChannel,
            registry: _consumerRegistry,
            consumerId: consumerId,
            topic: endpointName,
            logger: _logger);

        _consumerRegistry.Register(consumerId, kafkaConsumer);

        kafkaConsumer.StartLoop();

        LogConsumerRegistered(consumerId, endpointName);

        try
        {
            await foreach (InboundMessage message in inboundChannel.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            await kafkaConsumer.StopAsync().ConfigureAwait(false);
            await kafkaConsumer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Settlement is offset-based (Kafka has no per-message Ack/Nack):
    /// <list type="bullet">
    /// <item><term>Ack</term><description>Calls <c>IConsumer.StoreOffset(tpo with offset+1)</c>; librdkafka commits in background (D6). Map entry evicted.</description></item>
    /// <item><term>Nack / Requeue / Reject</term><description>Does NOT store offset; message will be re-consumed from last committed offset after restart or rebalance. Map entry evicted. <c>Reject</c> logs a warning (DLQ in R1.3).</description></item>
    /// <item><term>Defer</term><description>Throws <see cref="NotSupportedException"/> — requires retry-topic (R1.3).</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (action == SettlementAction.Defer)
        {
            throw new NotSupportedException("Defer wymaga retry-topic (R1.3).");
        }

        // Resolve the consumer that delivered this message via the injected BW-ConsumerId header.
        if (!message.Headers.TryGetValue("BW-ConsumerId", out string? consumerId) ||
            string.IsNullOrEmpty(consumerId))
        {
            throw new BareWireTransportException(
                message: "Cannot settle message: BW-ConsumerId header is missing. " +
                         "The message was not delivered by a KafkaConsumer managed by this adapter.",
                transportName: TransportName,
                endpointAddress: null);
        }

        KafkaConsumer? consumer = _consumerRegistry.ResolveByConsumerId(consumerId);

        if (consumer is null)
        {
            throw new BareWireTransportException(
                message: $"Cannot settle message: no active consumer with id '{consumerId}' found. " +
                         "The consumer may have been stopped or the message originated from a different adapter instance.",
                transportName: TransportName,
                endpointAddress: null);
        }

        // Evict the DeliveryTag → TopicPartitionOffset entry regardless of action
        // to ensure the map cannot grow unbounded (CLAUDE.md rule: no unbounded buffers).
        TopicPartitionOffset? tpo = _consumerRegistry.TryEvictOffset(consumerId, message.DeliveryTag);

        switch (action)
        {
            case SettlementAction.Ack:
                if (tpo is not null)
                {
                    // D6: Use explicit StoreOffset(TopicPartitionOffset) overload with offset+1.
                    // Do NOT use StoreOffset(ConsumeResult) to avoid accidental double +1 (GAP-5).
                    var offsetToStore = new TopicPartitionOffset(
                        tpo.TopicPartition,
                        tpo.Offset + 1);

                    consumer.NativeConsumer.StoreOffset(offsetToStore);
                }
                else
                {
                    LogMissingOffsetForAck(message.DeliveryTag, consumerId);
                }

                break;

            case SettlementAction.Nack:
            case SettlementAction.Requeue:
                // No-store: message will be re-consumed from last committed offset on restart/rebalance.
                LogNoStore(action, message.DeliveryTag, consumerId);
                break;

            case SettlementAction.Reject:
                // R1.2: behaves like Nack (no DLQ — DLQ comes in R1.3). Log a warning.
                LogRejectWithoutDlq(message.DeliveryTag, consumerId);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action,
                    $"Unknown SettlementAction value: {action}.");
        }

        return Task.CompletedTask;
    }

    // ── Consumer builder ──────────────────────────────────────────────────────

    private IConsumer<byte[], byte[]> BuildNativeConsumer(string consumerId)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.GroupId,
            AutoOffsetReset = _options.AutoOffsetReset,
            PartitionAssignmentStrategy = PartitionAssignmentStrategyResolver.Resolve(
                _options.ConsumerPartitionAssignmentStrategy),
            EnableAutoCommit = _options.EnableAutoCommit,
            EnableAutoOffsetStore = _options.EnableAutoOffsetStore,
        };

        if (_options.SessionTimeoutMs.HasValue)
        {
            config.SessionTimeoutMs = _options.SessionTimeoutMs.Value;
        }

        if (_options.MaxPollIntervalMs.HasValue)
        {
            config.MaxPollIntervalMs = _options.MaxPollIntervalMs.Value;
        }

        return new ConsumerBuilder<byte[], byte[]>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                LogPartitionsAssigned(consumerId, partitions.Count);
            })
            .SetPartitionsRevokedHandler((_, partitions) =>
            {
                LogPartitionsRevoked(consumerId, partitions.Count);
            })
            .SetErrorHandler((_, error) =>
            {
                LogKafkaError(error.Code, error.Reason, consumerId);
            })
            .Build();
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka consumer {ConsumerId} registered for topic '{Topic}'.")]
    private partial void LogConsumerRegistered(string consumerId, string topic);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka consumer {ConsumerId}: {PartitionCount} partition(s) assigned.")]
    private partial void LogPartitionsAssigned(string consumerId, int partitionCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka consumer {ConsumerId}: {PartitionCount} partition(s) revoked.")]
    private partial void LogPartitionsRevoked(string consumerId, int partitionCount);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Kafka error on consumer {ConsumerId}: code={ErrorCode}, reason={Reason}.")]
    private partial void LogKafkaError(ErrorCode errorCode, string reason, string consumerId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SettleAsync({Action}): not storing offset for DeliveryTag={DeliveryTag}, consumer={ConsumerId}.")]
    private partial void LogNoStore(SettlementAction action, ulong deliveryTag, string consumerId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SettleAsync(Reject): no DLQ configured — message DeliveryTag={DeliveryTag}, consumer={ConsumerId} will be re-consumed from last committed offset (DLQ support in R1.3).")]
    private partial void LogRejectWithoutDlq(ulong deliveryTag, string consumerId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SettleAsync(Ack): no TopicPartitionOffset found for DeliveryTag={DeliveryTag}, consumer={ConsumerId}. Offset not stored.")]
    private partial void LogMissingOffsetForAck(ulong deliveryTag, string consumerId);
}
