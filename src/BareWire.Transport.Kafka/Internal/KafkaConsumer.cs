using System.Buffers;
using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Bridges a Confluent.Kafka <see cref="IConsumer{TKey,TValue}"/> blocking poll loop into a
/// bounded <see cref="Channel{T}"/> of <see cref="InboundMessage"/> instances.
/// Analogous to <c>RabbitMqConsumer</c> in the RabbitMQ transport.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading model (D2):</b> The blocking <c>consumer.Consume(ct)</c> call runs on a dedicated
/// long-running thread (via <see cref="TaskCreationOptions.LongRunning"/>) rather than
/// <c>Task.Run</c>. A <c>Task.Run</c> call would occupy a thread-pool thread for the lifetime
/// of the consumer, starving the pool when many consumers are active.
/// </para>
/// <para>
/// <b>DeliveryTag (D1):</b> Uses a per-consumer monotonic <c>long</c> incremented by
/// <c>Interlocked.Increment</c>. The raw Kafka offset is unique only per-partition,
/// so a multi-partition subscription would produce collisions (e.g. p0/offset5 and p3/offset5
/// → same tag) and <c>StoreOffset</c> would commit the wrong partition. The delivery tag is
/// stored in <see cref="KafkaConsumerRegistry"/> alongside the <see cref="TopicPartitionOffset"/>
/// so <c>SettleAsync</c> can recover the correct TPO.
/// </para>
/// <para>
/// <b>Body ownership (D4 — deliberate divergence from RabbitMqConsumer.cs:48-49):</b>
/// <c>ConsumeResult.Message.Value</c> is a <c>byte[]</c> handed to the caller by ownership —
/// librdkafka does NOT free it after <c>Consume</c> returns (unlike RabbitMQ, which frees the
/// body memory after the handler completes). Wrapping without a copy avoids a second allocation
/// that would break the &lt;512 B/op consume budget. <c>pooledBuffer</c> is therefore
/// <see langword="null"/>; <see cref="InboundMessage.Dispose"/> is a no-op for the body.
/// </para>
/// <para>
/// <b>Back-pressure (D3):</b> When the channel is full the consumer pauses all assigned
/// partitions via <c>IConsumer.Pause</c> to prevent <c>max.poll.interval.ms</c> eviction,
/// then resumes when the channel drains below capacity. <c>FullMode.Wait</c> acts only as a
/// short bounded reserve while the pause propagates.
/// </para>
/// <para>
/// <b>Full consume/commit/rebalance behaviour</b> is covered by integration tests (R1.5 — Aspire
/// + real Kafka broker). The unit tests for this class target the pure, broker-free logic:
/// <see cref="MergeHeaders"/>, registry interactions, and option mapping.
/// </para>
/// </remarks>
internal sealed partial class KafkaConsumer : IAsyncDisposable
{
    /// <summary>
    /// Per-partition tracking limit used when no tracker is injected: the default
    /// <c>InternalQueueCapacity</c> (1000) + <c>MaxInFlightMessages</c> (100) + 2 (the message in the
    /// poll loop's hand and the one in the reader's hand).
    /// </summary>
    internal const int DefaultMaxTrackedPerPartition = 1_102;

    /// <summary>
    /// Consume timeout used while a partition is paused by the commit tracker, so the poll loop
    /// re-checks <see cref="KafkaPartitionCommitTracker.HasCapacity"/> and resumes the partition.
    /// </summary>
    private static readonly TimeSpan TrackerResumePollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IConsumer<byte[], byte[]> _consumer;
    private readonly Channel<InboundMessage> _channel;
    private readonly KafkaConsumerRegistry _registry;
    private readonly string _consumerId;
    private readonly string _topic;
    private readonly bool _isRetryOrDlqTopic;
    private readonly ILogger _logger;
    private readonly KafkaPartitionCommitTracker _commitTracker;

    /// <summary>
    /// Partitions paused by the commit tracker because they reached the per-partition tracking
    /// limit (D3). Touched only on the poll thread (poll loop and rebalance handlers).
    /// </summary>
    private readonly HashSet<TopicPartition> _pausedByTracker = [];

    private long _deliveryTagCounter;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    internal KafkaConsumer(
        IConsumer<byte[], byte[]> consumer,
        Channel<InboundMessage> channel,
        KafkaConsumerRegistry registry,
        string consumerId,
        string topic,
        ILogger logger,
        bool isRetryOrDlqTopic = false,
        KafkaPartitionCommitTracker? commitTracker = null)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(consumerId);
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(logger);

        _consumer = consumer;
        _channel = channel;
        _registry = registry;
        _consumerId = consumerId;
        _topic = topic;
        _isRetryOrDlqTopic = isRetryOrDlqTopic;
        _logger = logger;
        _commitTracker = commitTracker
            ?? new KafkaPartitionCommitTracker(topic, DefaultMaxTrackedPerPartition, consumer.StoreOffset);
    }

    /// <summary>
    /// Gets the unique id of this consumer instance (stamped as <c>BW-ConsumerId</c>).
    /// </summary>
    internal string ConsumerId => _consumerId;

    /// <summary>
    /// Gets the underlying Confluent.Kafka consumer.
    /// Used by <c>SettleAsync</c> to call <c>StoreOffset</c>.
    /// </summary>
    internal IConsumer<byte[], byte[]> NativeConsumer => _consumer;

    /// <summary>
    /// Gets the per-partition commit tracker. <c>SettleAsync</c> routes every offset settlement
    /// through it so the stored position only advances over a contiguous settled prefix (B17).
    /// </summary>
    internal KafkaPartitionCommitTracker CommitTracker => _commitTracker;

    /// <summary>
    /// Forgets partitions removed by a rebalance from the tracker-pause set. Invoked from the
    /// revoked handler, which Confluent.Kafka runs on the poll thread.
    /// </summary>
    internal void OnPartitionsRevoked(IEnumerable<TopicPartition> partitions)
    {
        foreach (TopicPartition partition in partitions)
        {
            _pausedByTracker.Remove(partition);
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the polling loop on a dedicated long-running thread (D2).
    /// </summary>
    internal void StartLoop()
    {
        _loopCts = new CancellationTokenSource();

        // D2: dedicated long-running thread so the blocking consumer.Consume(ct) call does not
        // occupy a thread-pool thread for the lifetime of this consumer.
        _loopTask = Task.Factory.StartNew(
            () => RunLoop(_loopCts.Token),
            _loopCts.Token,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Signals the polling loop to stop, then closes the consumer performing a clean
    /// group leave + final offset commit. Completes the channel writer so that
    /// <c>ReadAllAsync</c> on the reader side terminates.
    /// </summary>
    internal async Task StopAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — the loop was cancelled.
            }
            catch (Exception ex)
            {
                LogLoopStopError(ex);
            }
        }

        _channel.Writer.TryComplete();
        _registry.Unregister(_consumerId);

        try
        {
            // Clean leave-group and final offset commit.
            _consumer.Close();
        }
        catch (Exception ex)
        {
            LogConsumerCloseError(ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _consumer.Dispose();
        _loopCts?.Dispose();
    }

    // ── Polling loop ──────────────────────────────────────────────────────────

    private void RunLoop(CancellationToken cancellationToken)
    {
        _consumer.Subscribe(_topic);
        LogConsumerStarted(_consumerId, _topic);

        bool paused = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsumeResult<byte[], byte[]>? result;

                try
                {
                    // Blocking poll — returns null when no message is ready (e.g. end of partition
                    // when consumeResultFields includes end-of-partition events).
                    // While a partition is paused by the commit tracker the loop must wake up
                    // periodically to notice that settlements freed capacity (D3).
                    result = _pausedByTracker.Count > 0
                        ? _consumer.Consume(TrackerResumePollInterval)
                        : _consumer.Consume(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation requested — exit the loop cleanly.
                    break;
                }
                catch (ConsumeException ex)
                {
                    // Transient broker error — log and continue polling.
                    LogConsumeError(ex.Error.Code, ex.Error.Reason, ex);
                    continue;
                }

                // Null result means end-of-partition marker (when EOF events are enabled).
                // We don't request EOF events in our ConsumerConfig so this should not occur,
                // but guard defensively.
                if (_pausedByTracker.Count > 0)
                {
                    ResumeTrackerPausedPartitions(paused);
                }

                if (result is null || result.IsPartitionEOF)
                {
                    continue;
                }

                InboundMessage message = BuildMessage(result, out TopicPartitionOffset tpo);

                // B17: register the delivered offset; at the per-partition limit pause that partition
                // (back-pressure) instead of ever pinning the commit position.
                if (_commitTracker.Track(tpo.Partition.Value, tpo.Offset.Value))
                {
                    PauseForTracker(tpo.TopicPartition);
                }

                // D3 Pause/Resume: attempt a synchronous write first.
                if (_channel.Writer.TryWrite(message))
                {
                    // Fast path — wrote successfully.
                    if (paused)
                    {
                        // Channel has space again — resume all assigned partitions.
                        ResumeAll();
                        paused = false;
                    }
                }
                else
                {
                    // Channel is full — pause partitions to prevent poll-interval eviction (D3).
                    if (!paused)
                    {
                        PauseAll();
                        paused = true;
                        LogChannelFull(_consumerId);
                    }

                    // Block until the channel can accept the message.
                    // This is the bounded reserve mentioned in D3. The pause above should prevent
                    // librdkafka from pre-fetching more messages while we wait.
                    try
                    {
                        // ValueTask — synchronous fast path once capacity frees up.
                        ValueTask<bool> waitTask = _channel.Writer.WaitToWriteAsync(cancellationToken);
                        if (!waitTask.IsCompleted)
                        {
                            waitTask.AsTask().GetAwaiter().GetResult();
                        }
                        else
                        {
                            waitTask.GetAwaiter().GetResult();
                        }

                        // PERF-1: the TryWrite result MUST be checked. WaitToWriteAsync returns true
                        // immediately under a non-Wait FullMode (DropWrite/DropOldest/DropNewest — all
                        // settable on the public FlowControlOptions.FullMode), in which case TryWrite
                        // silently drops/evicts the message. The DeliveryTag → TopicPartitionOffset entry
                        // was already stored in BuildMessage, so a dropped message would (a) never be
                        // settled (silent at-least-once violation) and (b) leak its offset-map entry.
                        // On a failed write we therefore evict the offset entry and dispose the message,
                        // mirroring RabbitMqConsumer.cs:87-93 (which nack-requeues on a failed write).
                        if (!_channel.Writer.TryWrite(message))
                        {
                            _registry.TryEvictOffset(_consumerId, message.DeliveryTag);
                            HoldAbandoned(tpo);
                            message.Dispose();
                            LogMessageDropped(_consumerId, message.DeliveryTag);
                        }

                        // Resume after the channel accepted the backlog.
                        if (paused)
                        {
                            ResumeAll();
                            paused = false;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Disposing the message since nobody will consume it.
                        _registry.TryEvictOffset(_consumerId, message.DeliveryTag);
                        HoldAbandoned(tpo);
                        message.Dispose();
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        _registry.TryEvictOffset(_consumerId, message.DeliveryTag);
                        HoldAbandoned(tpo);
                        message.Dispose();
                        break;
                    }
                }
            }
        }
        finally
        {
            if (paused)
            {
                // Best-effort resume so the underlying consumer can do a final clean close.
                ResumeAll();
            }

            LogConsumerStopped(_consumerId, _topic);
        }
    }

    // ── Message construction ──────────────────────────────────────────────────

    private InboundMessage BuildMessage(ConsumeResult<byte[], byte[]> result, out TopicPartitionOffset tpo)
    {
        // D4: Wrap without copy. ConsumeResult.Message.Value is byte[] handed to the caller
        // by ownership — librdkafka does NOT free it after Consume returns (unlike RabbitMQ).
        // A copy would be a second allocation violating the <512 B/op budget. pooledBuffer=null.
        // (Deliberate divergence from RabbitMqConsumer.cs:48-49 — do NOT "fix" this to a copy.)
        byte[]? value = result.Message?.Value;
        ReadOnlySequence<byte> body = value is { Length: > 0 }
            ? new ReadOnlySequence<byte>(value)
            : ReadOnlySequence<byte>.Empty;

        Confluent.Kafka.Headers? kafkaHeaders = result.Message?.Headers;

        // D5: Inject BW-* STRICTLY AFTER MapInbound so that wire-level BW-ConsumerId cannot
        // spoof the consumer identity (last-write-wins, mirror RabbitMqConsumer.cs:64-71).
        Dictionary<string, string> headers = MergeHeaders(
            kafkaHeaders,
            topic: result.Topic,
            partition: result.Partition.Value,
            consumerId: _consumerId,
            isRetryOrDlqTopic: _isRetryOrDlqTopic);

        string messageId = headers.TryGetValue("message-id", out string? mappedId) && !string.IsNullOrEmpty(mappedId)
            ? mappedId
            : Guid.NewGuid().ToString("N");

        // D1: Per-consumer monotonic delivery tag. Raw Kafka offset is unique only per-partition;
        // a multi-partition subscription would produce collisions → StoreOffset on wrong partition.
        ulong deliveryTag = (ulong)Interlocked.Increment(ref _deliveryTagCounter);

        // Record DeliveryTag → TopicPartitionOffset so SettleAsync can commit the right offset.
        // PERF-4: ConsumeResult.TopicPartitionOffset allocates on every access — read it once.
        tpo = result.TopicPartitionOffset;
        _registry.StoreOffset(_consumerId, deliveryTag, tpo);

        return new InboundMessage(
            messageId: messageId,
            headers: headers,
            body: body,
            deliveryTag: deliveryTag,
            pooledBuffer: null);
    }

    /// <summary>
    /// Retry/DLQ tracking-header prefix (R1.3). These headers are stamped only by the library's own
    /// republication producer onto the retry/DLQ topics — never legitimately present on a source
    /// topic. They are stripped on source-topic consumption (SEC-1, ADR-010) so a producer to the
    /// source topic cannot spoof the retry count, dead-letter status, reason, or original-topic
    /// provenance.
    /// </summary>
    private static readonly string[] RetryDlqTrackingHeaders =
        ["BW-RetryCount", "BW-RetryAt", "BW-OriginalTopic", "BW-DeadLettered", "BW-DeadLetterReason"];

    /// <summary>
    /// Pure header-merge function: maps Kafka wire headers via <see cref="KafkaHeaderMapper.MapInbound"/>,
    /// then stamps BareWire routing headers (<c>BW-Topic</c>, <c>BW-Partition</c>,
    /// <c>BW-ConsumerId</c>) using the indexer (last-write-wins, D5). When the message was consumed
    /// from a source topic (not a retry/DLQ topic), the retry/DLQ tracking-header prefix is stripped
    /// first so a source-topic producer cannot spoof it (SEC-1, ADR-010).
    /// </summary>
    /// <remarks>
    /// Extracted as a <c>static internal</c> method so unit tests can verify the last-write-wins
    /// override of a spoofed <c>BW-ConsumerId</c> wire header — and the SEC-1 strip of spoofed
    /// retry/DLQ headers — without needing a live broker.
    /// </remarks>
    /// <param name="kafkaHeaders">The raw wire headers from the <c>ConsumeResult</c>.</param>
    /// <param name="topic">The Kafka topic name.</param>
    /// <param name="partition">The partition number from which the message was consumed.</param>
    /// <param name="consumerId">The BareWire consumer id to stamp.</param>
    /// <param name="isRetryOrDlqTopic">
    /// <see langword="true"/> when <paramref name="topic"/> is a retry/DLQ topic (where the
    /// library's own retry/DLQ tracking headers are legitimate and must be preserved);
    /// <see langword="false"/> for a source topic (where any retry/DLQ tracking header is
    /// untrusted and is stripped — SEC-1).
    /// </param>
    /// <returns>A merged header dictionary with BW-* values guaranteed to be authoritative.</returns>
    internal static Dictionary<string, string> MergeHeaders(
        Confluent.Kafka.Headers? kafkaHeaders,
        string topic,
        int partition,
        string consumerId,
        bool isRetryOrDlqTopic = false)
    {
        // Step 1: map all wire headers (may include attacker-supplied BW-* headers).
        Dictionary<string, string> headers = KafkaHeaderMapper.MapInbound(kafkaHeaders);

        // Step 2 (SEC-1): strip the retry/DLQ tracking-header prefix on source-topic consumption.
        // On a retry/DLQ topic these were stamped by the library's own producer and are kept.
        if (!isRetryOrDlqTopic)
        {
            foreach (string trackingHeader in RetryDlqTrackingHeaders)
            {
                headers.Remove(trackingHeader);
            }
        }

        // Step 3: overwrite with authoritative values AFTER the mapper (last-write-wins, D5).
        headers["BW-Topic"] = topic;
        headers["BW-Partition"] = partition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        headers["BW-ConsumerId"] = consumerId;

        return headers;
    }

    // ── Pause / Resume ────────────────────────────────────────────────────────

    private void PauseAll()
    {
        try
        {
            IEnumerable<TopicPartition> assigned = _consumer.Assignment;
            _consumer.Pause(assigned);
        }
        catch (Exception ex)
        {
            LogPauseError(ex);
        }
    }

    private void ResumeAll()
    {
        try
        {
            // Partitions paused by the commit tracker stay paused until they regain capacity.
            IEnumerable<TopicPartition> assigned = _pausedByTracker.Count == 0
                ? _consumer.Assignment
                : _consumer.Assignment.Where(tp => !_pausedByTracker.Contains(tp)).ToList();
            _consumer.Resume(assigned);
        }
        catch (Exception ex)
        {
            LogResumeError(ex);
        }
    }

    private void PauseForTracker(TopicPartition partition)
    {
        if (!_pausedByTracker.Add(partition))
        {
            return;
        }

        try
        {
            _consumer.Pause([partition]);
            LogPartitionPausedByTracker(_consumerId, partition.Topic, partition.Partition.Value);
        }
        catch (KafkaException ex)
        {
            _pausedByTracker.Remove(partition);
            LogPauseError(ex);
        }
    }

    private void ResumeTrackerPausedPartitions(bool channelPaused)
    {
        List<TopicPartition>? resumable = null;

        foreach (TopicPartition partition in _pausedByTracker)
        {
            if (_commitTracker.HasCapacity(partition.Partition.Value))
            {
                (resumable ??= []).Add(partition);
            }
        }

        if (resumable is null)
        {
            return;
        }

        foreach (TopicPartition partition in resumable)
        {
            _pausedByTracker.Remove(partition);
        }

        // When the whole consumer is paused for channel back-pressure, ResumeAll resumes these
        // partitions (they are no longer tracker-paused) once the channel drains.
        if (channelPaused)
        {
            return;
        }

        try
        {
            _consumer.Resume(resumable);
        }
        catch (KafkaException ex)
        {
            LogResumeError(ex);
        }
    }

    private void HoldAbandoned(TopicPartitionOffset tpo)
    {
        if (_commitTracker.Hold(tpo.Partition.Value, tpo.Offset.Value))
        {
            LogPartitionCommitHeld(_consumerId, tpo.Topic, tpo.Partition.Value, tpo.Offset.Value);
        }
    }

    // ── Logging (source-gen partial methods) ──────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka consumer {ConsumerId} started polling topic '{Topic}'.")]
    private partial void LogConsumerStarted(string consumerId, string topic);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka consumer {ConsumerId} stopped polling topic '{Topic}'.")]
    private partial void LogConsumerStopped(string consumerId, string topic);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka consumer {ConsumerId}: channel full — pausing partitions (D3 back-pressure).")]
    private partial void LogChannelFull(string consumerId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka consumer {ConsumerId}: message DeliveryTag={DeliveryTag} dropped by the bounded channel (non-Wait FullMode); offset entry evicted, message will be re-consumed from the last committed offset.")]
    private partial void LogMessageDropped(string consumerId, ulong deliveryTag);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka consumer {ConsumerId}: partition {Topic}[{Partition}] reached the unsettled-offset limit — pausing it until settlements free capacity.")]
    private partial void LogPartitionPausedByTracker(string consumerId, string topic, int partition);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka consumer {ConsumerId}: holding the commit position of {Topic}[{Partition}] at offset {Offset} because a delivered message was abandoned before it could be handled; it will be redelivered after a restart.")]
    private partial void LogPartitionCommitHeld(string consumerId, string topic, int partition, long offset);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Kafka consumer error: code={ErrorCode}, reason={Reason}.")]
    private partial void LogConsumeError(ErrorCode errorCode, string reason, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Kafka consumer loop terminated with an unexpected error.")]
    private partial void LogLoopStopError(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka consumer Close() threw an exception during shutdown.")]
    private partial void LogConsumerCloseError(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka consumer failed to pause partitions.")]
    private partial void LogPauseError(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka consumer failed to resume partitions.")]
    private partial void LogResumeError(Exception exception);
}
