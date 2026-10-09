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

        // B17 / D3: per-partition tracking limit derived from the flow-control options the runner
        // actually passed — queue capacity + messages in flight + the message in the poll loop's
        // hand + the one in the reader's hand (the +2).
        long trackingLimit = (long)flowControl.InternalQueueCapacity + flowControl.MaxInFlightMessages + 2;

        if (trackingLimit > KafkaPartitionCommitTracker.MaxTrackedPerPartitionCeiling)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(FlowControlOptions.InternalQueueCapacity),
                optionValue: flowControl.InternalQueueCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                expectedValue: "InternalQueueCapacity + MaxInFlightMessages + 2 <= " +
                    KafkaPartitionCommitTracker.MaxTrackedPerPartitionCeiling.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
        }

        string consumerId = Guid.NewGuid().ToString("N");

        // The native consumer does not exist yet when the tracker is created (the revoked handler
        // needs the tracker); the store callback closes over a local assigned right after the build.
        IConsumer<byte[], byte[]>? nativeConsumer = null;
        var commitTracker = new KafkaPartitionCommitTracker(
            endpointName,
            (int)trackingLimit,
            tpo => nativeConsumer!.StoreOffset(tpo));

        var inboundChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                FullMode = flowControl.FullMode,
                SingleWriter = true,
                SingleReader = false,
            },
            itemDropped: dropped =>
            {
                // Drop* full modes silently evict a delivered message: it will never be settled, so
                // pin the commit position at its offset (redelivered after restart) and free the entry.
                TopicPartitionOffset? droppedTpo = _consumerRegistry.TryEvictOffset(consumerId, dropped.DeliveryTag);
                if (droppedTpo is not null &&
                    commitTracker.Hold(droppedTpo.Partition.Value, droppedTpo.Offset.Value))
                {
                    LogPartitionCommitHeld(consumerId, droppedTpo.Topic, droppedTpo.Partition.Value, droppedTpo.Offset.Value);
                }

                dropped.Dispose();
            });

        KafkaConsumer? kafkaConsumerRef = null;
        nativeConsumer = BuildNativeConsumer(consumerId, commitTracker, () => kafkaConsumerRef);

        if (_options.EnableAutoOffsetStore)
        {
            LogAutoOffsetStoreBypassesCommitTracker(consumerId);
        }

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
            isRetryOrDlqTopic: isRetryOrDlqTopic,
            commitTracker: commitTracker);
        kafkaConsumerRef = kafkaConsumer;

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
    /// Settlement is offset-based (Kafka has no per-message Ack/Nack). The stored (committed)
    /// position of a partition only ever advances over a <b>contiguous prefix</b> of messages that
    /// were settled "forward", tracked by <see cref="KafkaPartitionCommitTracker"/>; an <c>Ack</c>
    /// of a higher offset therefore never commits past a message that was returned. When the
    /// retry/DLQ pattern is <b>disabled</b> (default — opt-in, ADR-002/ADR-010):
    /// <list type="bullet">
    /// <item><term>Ack</term><description>Stores <c>offset + 1</c> only when the contiguous prefix advances; librdkafka commits in background (D6).</description></item>
    /// <item><term>Nack / Requeue</term><description>Pins the partition commit position at this offset until the consumer restarts; the message (and any later messages of the partition) is redelivered after a restart. Later messages of the partition may therefore be delivered again — consumers must be idempotent. There is no redelivery within the same session.</description></item>
    /// <item><term>Reject</term><description>Terminal: the offset is settled forward and a Warning is logged (no dead-letter destination exists).</description></item>
    /// <item><term>Defer</term><description>Throws <see cref="NotSupportedException"/> — requires the retry-topic to be enabled.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// When the retry/DLQ pattern is <b>enabled</b> (ADR-010), failure actions are routed via
    /// <see cref="KafkaSettlementRouter"/>: <c>Defer</c> republishes to the retry-topic (exponential
    /// backoff) while attempts remain, else to the DLQ-topic; <c>Reject</c> dead-letters immediately;
    /// <c>Nack</c> below the cap is republished to the retry-topic as one more attempt, at the cap it
    /// dead-letters (poison guard); <c>Requeue</c> pins the commit position like above. On every
    /// republish path the source offset is settled AFTER the republication is confirmed (D2 —
    /// republish-then-store) so a failed republish does not lose the message. The wire-supplied
    /// <c>BW-RetryCount</c> is clamped to <c>[0, MaxRetryCount]</c> before routing (SEC-1).
    /// </para>
    /// <para>
    /// A partition with too many unsettled offsets (<c>InternalQueueCapacity + MaxInFlightMessages + 2</c>)
    /// is paused and resumed once settlements free capacity (back-pressure); the commit position is
    /// never pinned by slowness alone.
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
                    CompleteSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
                    break;

                case SettlementAction.Nack:
                case SettlementAction.Requeue:
                    HoldSourceOffset(consumer, tpo, consumerId);
                    LogNoStore(action, message.DeliveryTag, consumerId);
                    break;

                case SettlementAction.Reject:
                    // D1: terminal forward settlement — there is no dead-letter destination, and one
                    // unhandled message must not freeze the partition commit position.
                    if (tpo is not null)
                    {
                        LogRejectCommittedWithoutDlq(consumerId, tpo.Topic, tpo.Partition.Value, tpo.Offset.Value);
                    }

                    CompleteSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
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
                CompleteSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
                break;

            case SettlementOutcome.NoStore:
                HoldSourceOffset(consumer, tpo, consumerId);
                LogNoStore(action, message.DeliveryTag, consumerId);
                break;

            case SettlementOutcome.RepublishRetryThenStore:
                // D2: await republication FIRST, then store the source offset so a failed
                // republish does not advance the source offset (no message loss).
                await GetOrCreateRetryDlqProducer()
                    .RepublishToRetryAsync(message, sourceTopic, clampedRetryCount, retryDlq, cancellationToken)
                    .ConfigureAwait(false);
                CompleteSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
                LogRepublishedToRetry(message.DeliveryTag, consumerId, clampedRetryCount + 1);
                break;

            case SettlementOutcome.RepublishDlqThenStore:
                string reason = ResolveDeadLetterReason(action, clampedRetryCount, retryDlq.MaxRetryCount);
                await GetOrCreateRetryDlqProducer()
                    .RepublishToDlqAsync(message, sourceTopic, reason, retryDlq, cancellationToken)
                    .ConfigureAwait(false);
                CompleteSourceOffset(consumer, tpo, message.DeliveryTag, consumerId);
                LogDeadLettered(message.DeliveryTag, consumerId, reason);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown SettlementOutcome value: {outcome}.");
        }
    }

    // ── SettleAsync helpers (R1.3) ─────────────────────────────────────────────

    private void CompleteSourceOffset(
        KafkaConsumer consumer, TopicPartitionOffset? tpo, ulong deliveryTag, string consumerId)
    {
        if (tpo is not null)
        {
            // B17: the tracker stores offset + 1 itself, and only when the contiguous settled prefix
            // of the partition advances (under the partition lock, so the position never regresses).
            consumer.CommitTracker.Complete(tpo.Partition.Value, tpo.Offset.Value);
        }
        else
        {
            LogMissingOffsetForAck(deliveryTag, consumerId);
        }
    }

    private void HoldSourceOffset(KafkaConsumer consumer, TopicPartitionOffset? tpo, string consumerId)
    {
        if (tpo is not null &&
            consumer.CommitTracker.Hold(tpo.Partition.Value, tpo.Offset.Value))
        {
            // Logged once per partition per assignment (SEC-8) — never the body or headers.
            LogPartitionCommitHeld(consumerId, tpo.Topic, tpo.Partition.Value, tpo.Offset.Value);
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

    private IConsumer<byte[], byte[]> BuildNativeConsumer(
        string consumerId,
        KafkaPartitionCommitTracker commitTracker,
        Func<KafkaConsumer?> consumerAccessor)
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

                // B17: forget tracker state of the revoked (or lost) partitions so a late settlement
                // can neither store an offset on a partition this member no longer owns nor leak
                // into the next assignment. Confluent.Kafka invokes this handler for lost partitions too.
                commitTracker.Revoke(partitions.Select(p => p.Partition.Value));
                consumerAccessor()?.OnPartitionsRevoked(partitions.Select(p => p.TopicPartition));
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
        Message = "SettleAsync({Action}): holding the partition commit position at this offset until restart for DeliveryTag={DeliveryTag}, consumer={ConsumerId}.")]
    private partial void LogNoStore(SettlementAction action, ulong deliveryTag, string consumerId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SettleAsync(Reject): no dead-letter destination configured — rejected message on {Topic}[{Partition}] offset {Offset}, consumer={ConsumerId} is committed and will not be redelivered.")]
    private partial void LogRejectCommittedWithoutDlq(string consumerId, string topic, int partition, long offset);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka consumer {ConsumerId}: commit position of {Topic}[{Partition}] is held at offset {Offset} (a message was returned, nacked or dropped); the message and later messages of the partition are redelivered after a consumer restart.")]
    private partial void LogPartitionCommitHeld(string consumerId, string topic, int partition, long offset);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka consumer {ConsumerId}: EnableAutoOffsetStore is on — librdkafka stores offsets itself, so the contiguous-prefix commit guarantee does not apply.")]
    private partial void LogAutoOffsetStoreBypassesCommitTracker(string consumerId);

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
