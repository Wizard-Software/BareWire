using System.Text;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Kafka;

/// <summary>
/// Kafka transport adapter. Producer side implemented in R1.1; consumer side
/// (<see cref="ConsumeAsync"/>, <see cref="SettleAsync"/>) implemented in R1.2.
/// Topology deployment (<see cref="DeployTopologyAsync"/>) implemented in R1.4.
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
    private IAdminClient? _adminClient;
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

    // ConsumeAsync and SettleAsync are implemented in KafkaTransportAdapter.Consumer.cs (R1.2/R1.3).

    /// <summary>
    /// Publishes a single retry/DLQ republication message, reusing the shared idempotent producer
    /// (D3). Called by <see cref="RetryDlqPublisher"/> from the retry/DLQ producer (R1.3).
    /// </summary>
    /// <remarks>
    /// Republication is a synchronous failure-path produce (<c>await ProduceAsync</c>) with
    /// <c>Acks.All</c> + idempotence (ADR-008). It is intentionally NOT on the hot Ack path and is
    /// excluded from the &gt;300K msgs/s throughput goal (ADR-009/ADR-010).
    /// </remarks>
    private async Task PublishRepublicationAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        byte[]? partitionKey = message.Headers.TryGetValue(PartitionKeyHeader, out string? keyValue)
            ? Encoding.UTF8.GetBytes(keyValue)
            : null;

        byte[] body = message.Body.ToArray();
        Headers kafkaHeaders = KafkaHeaderMapper.MapOutbound(message.Headers);

        var kafkaMessage = new Message<byte[], byte[]>
        {
            Key = partitionKey!,
            Value = body,
            Headers = kafkaHeaders,
        };

        try
        {
            await _producer!.ProduceAsync(message.RoutingKey, kafkaMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BareWireTransportException(
                message: $"Failed to republish message to retry/DLQ topic '{message.RoutingKey}'.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }
    }

    /// <summary>
    /// Adapter from <see cref="IRetryDlqPublisher"/> to <see cref="PublishRepublicationAsync"/>,
    /// so the retry/DLQ producer (R1.3) can publish through the shared idempotent producer without
    /// depending on the adapter type directly (D4 — keeps the producer unit-testable in isolation).
    /// </summary>
    private sealed class RetryDlqPublisher(KafkaTransportAdapter adapter) : IRetryDlqPublisher
    {
        public Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken) =>
            adapter.PublishRepublicationAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Creates Kafka topics from <see cref="TopologyDeclaration.Queues"/> using the admin client.
    /// Each queue declaration maps to a Kafka topic; topic-specific parameters (partition count,
    /// replication factor, retention) are read from <c>QueueDeclaration.Arguments</c> via
    /// <c>KafkaTopologyArguments</c> (D2). Exchanges and bindings are accepted (shared contract)
    /// but produce no admin operations — Kafka has no exchange or binding concept (D1).
    /// Topic-already-exists errors are silently swallowed (idempotent declaration, D3).
    /// </remarks>
    public async Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureAdminClientAsync(cancellationToken).ConfigureAwait(false);

        foreach (QueueDeclaration queue in topology.Queues)
        {
            KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

            var topicSpec = new TopicSpecification
            {
                Name = queue.Name,
                NumPartitions = spec.NumPartitions,
                ReplicationFactor = spec.ReplicationFactor,
                Configs = spec.Configs,
            };

            try
            {
                await _adminClient!.CreateTopicsAsync([topicSpec]).ConfigureAwait(false);
                LogTopicCreated(queue.Name);
            }
            catch (CreateTopicsException ex)
            {
                bool allAlreadyExist = ex.Results.TrueForAll(
                    r => r.Error.Code == ErrorCode.TopicAlreadyExists);

                if (allAlreadyExist)
                {
                    LogTopicAlreadyExists(queue.Name);
                }
                else
                {
                    string firstReason = ex.Results
                        .Find(r => r.Error.Code != ErrorCode.TopicAlreadyExists)
                        ?.Error.Reason ?? ex.Message;

                    throw new TopologyDeploymentException(
                        topologyElement: queue.Name,
                        transportName: TransportName,
                        brokerError: firstReason,
                        endpointAddress: null,
                        innerException: ex);
                }
            }
        }

        foreach (ExchangeDeclaration exchange in topology.Exchanges)
        {
            LogExchangeSkipped(exchange.Name);
        }

        foreach (ExchangeQueueBinding binding in topology.ExchangeQueueBindings)
        {
            LogBindingSkipped(binding.ExchangeName, binding.QueueName);
        }

        foreach (ExchangeExchangeBinding binding in topology.ExchangeExchangeBindings)
        {
            LogBindingSkipped(binding.SourceExchangeName, binding.DestinationExchangeName);
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

        // GAP-3: Stop all active consumers BEFORE disposing the producer and connection lock.
        // This ensures a clean leave-group + final offset commit for each consumer.
        foreach (Internal.KafkaConsumer consumer in _consumerRegistry.AllConsumers())
        {
            await consumer.StopAsync().ConfigureAwait(false);
            await consumer.DisposeAsync().ConfigureAwait(false);
        }

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

        // IAdminClient is IDisposable (not IAsyncDisposable) — dispose synchronously.
        _adminClient?.Dispose();
        _adminClient = null;

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

    private async Task EnsureAdminClientAsync(CancellationToken cancellationToken)
    {
        // Fast path: admin client already built.
        if (_adminClient is not null)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock — another caller may have built the admin client.
            if (_adminClient is not null)
            {
                return;
            }

            var config = new AdminClientConfig
            {
                BootstrapServers = _options.BootstrapServers,
            };

            _adminClient = new AdminClientBuilder(config).Build();

            LogAdminClientCreated(_options.BootstrapServers);
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka admin client created. BootstrapServers: {BootstrapServers}.")]
    private partial void LogAdminClientCreated(string bootstrapServers);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka topic '{TopicName}' created successfully.")]
    private partial void LogTopicCreated(string topicName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka topic '{TopicName}' already exists — skipping (idempotent declaration).")]
    private partial void LogTopicAlreadyExists(string topicName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka topology deploy: exchange '{ExchangeName}' skipped — Kafka has no exchange concept.")]
    private partial void LogExchangeSkipped(string exchangeName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Kafka topology deploy: binding '{SourceName}' -> '{DestinationName}' skipped — Kafka has no binding concept.")]
    private partial void LogBindingSkipped(string sourceName, string destinationName);
}
