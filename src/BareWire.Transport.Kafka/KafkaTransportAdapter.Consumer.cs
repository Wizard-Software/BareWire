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

    /// <summary>
    /// Retry/DLQ republication producer (R1.3). Lazily created on first use, reusing the shared
    /// idempotent producer via <see cref="RetryDlqPublisher"/> (D3/D4).
    /// </summary>
    private KafkaRetryDlqProducer? _retryDlqProducer;
    private readonly object _retryDlqProducerLock = new();

    private KafkaRetryDlqProducer GetOrCreateRetryDlqProducer()
    {
        if (_retryDlqProducer is not null)
        {
            return _retryDlqProducer;
        }

        lock (_retryDlqProducerLock)
        {
            _retryDlqProducer ??= new KafkaRetryDlqProducer(new RetryDlqPublisher(this));
            return _retryDlqProducer;
        }
    }

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

        // SEC-1: a subscribed topic that is itself a retry/DLQ topic carries legitimate
        // library-stamped tracking headers; a source topic does not (they get stripped).
        bool isRetryOrDlqTopic = IsRetryOrDlqTopic(endpointName);

        var kafkaConsumer = new KafkaConsumer(
            consumer: nativeConsumer,
            channel: inboundChannel,
            registry: _consumerRegistry,
            consumerId: consumerId,
            topic: endpointName,
            logger: _logger,
            isRetryOrDlqTopic: isRetryOrDlqTopic);

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
    /// Settlement is offset-based (Kafka has no per-message Ack/Nack). When the retry/DLQ pattern
    /// is <b>disabled</b> (default — opt-in, ADR-002/ADR-010):
    /// <list type="bullet">
    /// <item><term>Ack</term><description>Calls <c>IConsumer.StoreOffset(tpo with offset+1)</c>; librdkafka commits in background (D6).</description></item>
    /// <item><term>Nack / Requeue</term><description>Does NOT store offset; message re-consumed from last committed offset after restart/rebalance.</description></item>
    /// <item><term>Reject</term><description>Logs a warning and does NOT store offset (R1.2 back-compat).</description></item>
    /// <item><term>Defer</term><description>Throws <see cref="NotSupportedException"/> — requires the retry-topic to be enabled.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// When the retry/DLQ pattern is <b>enabled</b> (ADR-010), failure actions are routed via
    /// <see cref="KafkaSettlementRouter"/>: <c>Defer</c> republishes to the retry-topic (exponential
    /// backoff) while attempts remain, else to the DLQ-topic; <c>Reject</c> dead-letters immediately;
    /// <c>Nack</c> below the cap stays no-store, at the cap dead-letters (poison guard). On every
    /// republish path the source offset is stored AFTER the republication is confirmed (D2 —
    /// republish-then-store) so a failed republish does not lose the message. The wire-supplied
    /// <c>BW-RetryCount</c> is clamped to <c>[0, MaxRetryCount]</c> before routing (SEC-1).
    /// </para>
    /// <para>The offset-map entry is evicted exactly once, before routing, regardless of action (no unbounded buffers).</para>
    /// </remarks>
    public async Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool retryDlqEnabled = _options.RetryDlq.Enabled;

        // Back-compat (R1.2): when the retry/DLQ pattern is disabled, Defer is unsupported.
        if (action == SettlementAction.Defer && !retryDlqEnabled)
        {
            throw new NotSupportedException(
                "Defer wymaga włączonego wzorca retry-topic (ConfigureRetryDlq(r => r.Enable()), R1.3/ADR-010).");
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

        // Evict the DeliveryTag → TopicPartitionOffset entry ONCE, before routing, regardless of
        // action (PERF-1: single eviction point — all branches inherit it; no unbounded buffers).
        TopicPartitionOffset? tpo = _consumerRegistry.TryEvictOffset(consumerId, message.DeliveryTag);

        // ── Disabled-pattern fast path (R1.2 behaviour) ──────────────────────────
        if (!retryDlqEnabled)
        {
            switch (action)
            {
                case SettlementAction.Ack:
                    StoreSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
                    break;

                case SettlementAction.Nack:
                case SettlementAction.Requeue:
                    LogNoStore(action, message.DeliveryTag, consumerId);
                    break;

                case SettlementAction.Reject:
                    LogRejectWithoutDlq(message.DeliveryTag, consumerId);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action,
                        $"Unknown SettlementAction value: {action}.");
            }

            return;
        }

        // ── Enabled-pattern routing (R1.3/ADR-010) ───────────────────────────────
        KafkaRetryDlqOptions retryDlq = _options.RetryDlq;

        // SEC-1: clamp the untrusted wire BW-RetryCount before it can influence routing.
        int wireRetryCount = ReadWireRetryCount(message);
        int clampedRetryCount = KafkaSettlementRouter.ClampRetryCount(wireRetryCount, retryDlq.MaxRetryCount);

        SettlementOutcome outcome = KafkaSettlementRouter.Decide(action, clampedRetryCount, retryDlq.MaxRetryCount);

        // The topic the message was consumed from (authoritative, stamped by the consumer in R1.2).
        string sourceTopic = ResolveSourceTopic(message, tpo);

        switch (outcome)
        {
            case SettlementOutcome.StoreOffset:
                StoreSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
                break;

            case SettlementOutcome.NoStore:
                LogNoStore(action, message.DeliveryTag, consumerId);
                break;

            case SettlementOutcome.RepublishRetryThenStore:
                // D2: await republication FIRST, then store the source offset so a failed
                // republish does not advance the source offset (no message loss).
                await GetOrCreateRetryDlqProducer()
                    .RepublishToRetryAsync(message, sourceTopic, clampedRetryCount, retryDlq, cancellationToken)
                    .ConfigureAwait(false);
                StoreSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
                LogRepublishedToRetry(message.DeliveryTag, consumerId, clampedRetryCount + 1);
                break;

            case SettlementOutcome.RepublishDlqThenStore:
                string reason = ResolveDeadLetterReason(action, clampedRetryCount, retryDlq.MaxRetryCount);
                await GetOrCreateRetryDlqProducer()
                    .RepublishToDlqAsync(message, sourceTopic, reason, retryDlq, cancellationToken)
                    .ConfigureAwait(false);
                StoreSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
                LogDeadLettered(message.DeliveryTag, consumerId, reason);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown SettlementOutcome value: {outcome}.");
        }
    }

    // ── SettleAsync helpers (R1.3) ─────────────────────────────────────────────

    private void StoreSourceOffset(
        KafkaConsumer consumer, TopicPartitionOffset? tpo, ulong deliveryTag, string consumerId)
    {
        if (tpo is not null)
        {
            // D6: explicit StoreOffset(TopicPartitionOffset) with offset+1 (avoid double +1, GAP-5).
            var offsetToStore = new TopicPartitionOffset(tpo.TopicPartition, tpo.Offset + 1);
            consumer.NativeConsumer.StoreOffset(offsetToStore);
        }
        else
        {
            LogMissingOffsetForAck(deliveryTag, consumerId);
        }
    }

    /// <summary>
    /// Reads the wire <c>BW-RetryCount</c> header (untrusted). A missing or non-numeric value is
    /// treated as 0; the value is clamped by the caller (SEC-1) so a spoofed value is harmless here.
    /// </summary>
    private static int ReadWireRetryCount(InboundMessage message) =>
        message.Headers.TryGetValue(KafkaRetryDlqProducer.RetryCountHeader, out string? raw) &&
        int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;

    /// <summary>
    /// Resolves the source topic for republication. Prefers the authoritative <c>BW-Topic</c>
    /// header stamped by the consumer (R1.2 D5, last-write-wins); falls back to the evicted TPO's
    /// topic when available.
    /// </summary>
    private static string ResolveSourceTopic(InboundMessage message, TopicPartitionOffset? tpo)
    {
        if (message.Headers.TryGetValue("BW-Topic", out string? topic) && !string.IsNullOrEmpty(topic))
        {
            return topic;
        }

        return tpo?.Topic ?? throw new BareWireTransportException(
            message: "Cannot republish message: source topic could not be resolved " +
                     "(missing BW-Topic header and no TopicPartitionOffset).",
            transportName: "Kafka",
            endpointAddress: null);
    }

    private static string ResolveDeadLetterReason(SettlementAction action, int retryCount, int maxRetryCount) =>
        action switch
        {
            SettlementAction.Reject => KafkaRetryDlqProducer.DeadLetterReason.Rejected,
            SettlementAction.Defer => KafkaRetryDlqProducer.DeadLetterReason.RetryExhausted,
            SettlementAction.Nack => KafkaRetryDlqProducer.DeadLetterReason.NackExhausted,
            _ => KafkaRetryDlqProducer.DeadLetterReason.Rejected,
        };

    /// <summary>
    /// Determines whether <paramref name="topic"/> is a retry/DLQ topic (so its library-stamped
    /// tracking headers are legitimate and preserved on consumption — SEC-1). Only meaningful when
    /// the retry/DLQ pattern is enabled; returns <see langword="false"/> otherwise.
    /// </summary>
    private bool IsRetryOrDlqTopic(string topic)
    {
        KafkaRetryDlqOptions retryDlq = _options.RetryDlq;

        if (!retryDlq.Enabled)
        {
            return false;
        }

        return topic.EndsWith(retryDlq.RetryTopicSuffix, StringComparison.Ordinal)
            || topic.EndsWith(retryDlq.DlqTopicSuffix, StringComparison.Ordinal);
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SettleAsync: republished DeliveryTag={DeliveryTag}, consumer={ConsumerId} to retry-topic (attempt {RetryCount}); source offset stored.")]
    private partial void LogRepublishedToRetry(ulong deliveryTag, string consumerId, int retryCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SettleAsync: dead-lettered DeliveryTag={DeliveryTag}, consumer={ConsumerId} to DLQ-topic (reason={Reason}); source offset stored.")]
    private partial void LogDeadLettered(ulong deliveryTag, string consumerId, string reason);
}
