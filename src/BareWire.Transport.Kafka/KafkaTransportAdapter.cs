using System.Text;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Kafka;

/// <summary>
/// Kafka transport adapter implementing the producer side (R1.1).
/// Consumer side (<see cref="ConsumeAsync"/>), settlement, and topology deployment
/// are stubs that will be implemented in R1.2 / R1.4 respectively.
/// </summary>
/// <remarks>
/// <para>
/// Uses a single, long-lived, thread-safe <see cref="IProducer{TKey,TValue}"/> instance,
/// constructed lazily on the first <see cref="SendBatchAsync"/> call under a
/// <see cref="SemaphoreSlim"/> double-check lock (mirror of RabbitMQ adapter pattern).
/// </para>
/// <para>
/// <b>ADR-003 deviation:</b> <see cref="OutboundMessage.Body"/> is
/// <see cref="ReadOnlyMemory{T}"/> but Confluent.Kafka 2.x <c>Message&lt;byte[],byte[]&gt;.Value</c>
/// requires <c>byte[]</c>. A <c>.ToArray()</c> copy is therefore forced by the library API.
/// This ścieżka is excluded from the &lt;768 B/msg publish allocation budget.
/// </para>
/// </remarks>
internal sealed partial class KafkaTransportAdapter : ITransportAdapter, IAsyncDisposable
{
    /// <summary>
    /// Header name used to carry the Kafka partition key in BareWire outbound messages.
    /// When present, its UTF-8 bytes are used as the Kafka message key, enabling per-key ordering.
    /// </summary>
    private const string PartitionKeyHeader = "BW-PartitionKey";

    private readonly KafkaTransportOptions _options;
    private readonly ILogger<KafkaTransportAdapter> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private long _deliveryTagCounter;
    private IProducer<byte[], byte[]>? _producer;
    private bool _disposed;

    public KafkaTransportAdapter(
        KafkaTransportOptions options,
        ILogger<KafkaTransportAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TransportName => "Kafka";

    /// <inheritdoc />
    public TransportCapabilities Capabilities =>
        TransportCapabilities.OrderingKeys |
        TransportCapabilities.BatchReceive |
        TransportCapabilities.ExactlyOnce;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new BareWireTransportException(
                message: "Failed to establish Kafka producer connection before sending batch.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        // PERF-1: collect all produce tasks without awaiting in the loop, then await Task.WhenAll
        // to allow librdkafka's internal batching (linger.ms / batch.size) to take effect.
        // Sequential await-per-message would serialize the batch to ~1/RTT and defeat batching.
        var tasks = new Task<DeliveryResult<byte[], byte[]>>[messages.Count];

        for (int i = 0; i < messages.Count; i++)
        {
            OutboundMessage outbound = messages[i];

            // Partition key: read once from the BW-PartitionKey header if present (UTF-8 → byte[]).
            // When absent, null key → round-robin partitioning.
            byte[]? partitionKey = outbound.Headers.TryGetValue(PartitionKeyHeader, out string? keyValue)
                ? Encoding.UTF8.GetBytes(keyValue)
                : null;

            // ADR-003 deviation: Confluent.Kafka 2.x byte[] producer has no ReadOnlyMemory overload;
            // copy is forced by the library API.
            byte[] body = outbound.Body.ToArray();

            Headers kafkaHeaders = KafkaHeaderMapper.MapOutbound(outbound.Headers);

            var message = new Message<byte[], byte[]>
            {
                Key = partitionKey!,
                Value = body,
                Headers = kafkaHeaders,
            };

            tasks[i] = _producer!.ProduceAsync(outbound.RoutingKey, message, cancellationToken);
        }

        // Await all produce tasks together — preserves ordering contract: results[i] = result for messages[i]
        DeliveryResult<byte[], byte[]>[] deliveryResults;
        try
        {
            deliveryResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BareWireTransportException(
                message: "One or more messages in the batch failed during Kafka produce.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        var results = new SendResult[messages.Count];

        for (int i = 0; i < messages.Count; i++)
        {
            DeliveryResult<byte[], byte[]> result = deliveryResults[i];

            bool isConfirmed = result.Status == PersistenceStatus.Persisted;

            // Use the Kafka partition offset as the delivery tag when available.
            // Fall back to a monotonic counter (matching RabbitMQ adapter pattern)
            // for special offsets (Beginning, End, Stored, Unset).
            ulong deliveryTag = result.Offset.IsSpecial
                ? (ulong)Interlocked.Increment(ref _deliveryTagCounter)
                : (ulong)result.Offset.Value;

            results[i] = new SendResult(IsConfirmed: isConfirmed, DeliveryTag: deliveryTag);
        }

        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Stub — not implemented in R1.1.</b>
    /// Consumer side will be implemented in task R1.2.
    /// </remarks>
    public IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "ConsumeAsync is not implemented in R1.1 (producer only). " +
            "Consumer group support will be added in task R1.2.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Stub — not implemented in R1.1.</b>
    /// Settlement will be implemented in task R1.2.
    /// </remarks>
    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "SettleAsync is not implemented in R1.1 (producer only). " +
            "Message settlement will be added in task R1.2.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Stub — not implemented in R1.1.</b>
    /// Kafka admin client topology deployment will be implemented in task R1.4.
    /// </remarks>
    public Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "DeployTopologyAsync is not implemented in R1.1 (producer only). " +
            "Kafka admin client topic/partition management will be added in task R1.4.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_producer is not null)
        {
            // PERF-5: flush pending messages before disposing. Flush returns the number of
            // messages still in the queue (i.e. NOT yet delivered) after the timeout.
            int unFlushed = _producer.Flush(_options.FlushTimeout);

            if (unFlushed > 0)
            {
                LogUnflushedMessages(unFlushed, _options.FlushTimeout);
            }

            _producer.Dispose();
            _producer = null;
        }

        _connectionLock.Dispose();

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        // Fast path: producer already built (no lock needed for read — volatile-like via _disposed guard above)
        if (_producer is not null)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock — another caller may have built the producer
            if (_producer is not null)
            {
                return;
            }

            var config = new ProducerConfig
            {
                BootstrapServers = _options.BootstrapServers,
                EnableIdempotence = _options.EnableIdempotence,
                Acks = _options.Acks,
                MaxInFlight = _options.MaxInFlight,
            };

            // PERF-4: assign optional properties only when non-null to avoid coercing unset to 0,
            // which would degrade batching performance relative to librdkafka defaults.
            if (_options.MessageSendMaxRetries.HasValue)
            {
                config.MessageSendMaxRetries = _options.MessageSendMaxRetries.Value;
            }

            if (_options.LingerMs.HasValue)
            {
                config.LingerMs = _options.LingerMs.Value;
            }

            if (_options.BatchSize.HasValue)
            {
                config.BatchSize = _options.BatchSize.Value;
            }

            if (_options.QueueBufferingMaxMessages.HasValue)
            {
                config.QueueBufferingMaxMessages = _options.QueueBufferingMaxMessages.Value;
            }

            if (_options.QueueBufferingMaxKbytes.HasValue)
            {
                config.QueueBufferingMaxKbytes = _options.QueueBufferingMaxKbytes.Value;
            }

            _producer = new ProducerBuilder<byte[], byte[]>(config).Build();

            LogProducerCreated(_options.BootstrapServers);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka idempotent producer created. BootstrapServers: {BootstrapServers}.")]
    private partial void LogProducerCreated(string bootstrapServers);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Kafka producer disposed with {UnflushedCount} message(s) still in the queue after {FlushTimeout} flush timeout. These messages may be lost.")]
    private partial void LogUnflushedMessages(int unflushedCount, TimeSpan flushTimeout);
}
