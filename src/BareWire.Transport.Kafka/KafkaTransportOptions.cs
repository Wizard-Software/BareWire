using BareWire.Abstractions.Exceptions;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;

namespace BareWire.Transport.Kafka;

/// <summary>
/// Configuration options for the Kafka transport adapter.
/// Apply via <see cref="ServiceCollectionExtensions.AddBareWireKafka"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope note (R1.1/R1.2):</b> Producer-side configuration was added in R1.1.
/// Consumer-side configuration (<see cref="GroupId"/>, <see cref="AutoOffsetReset"/>,
/// <see cref="ConsumerPartitionAssignmentStrategy"/>, <see cref="EnableAutoCommit"/>,
/// <see cref="EnableAutoOffsetStore"/>, <see cref="SessionTimeoutMs"/>,
/// <see cref="MaxPollIntervalMs"/>) was added in R1.2.
/// SASL/SSL (<c>SecurityProtocol</c>, SCRAM, OAUTHBEARER) are deferred to a dedicated security task
/// (R1.x). The adapter defaults to <c>SecurityProtocol=Plaintext</c> — <b>do not use against a
/// production broker until the secure-config layer is in place.</b>
/// </para>
/// <para>
/// When future releases add SASL credentials, any new credential properties must carry an XML-doc
/// annotation "Never logged or included in diagnostic output", be excluded from <c>ToString()</c>/
/// diagnostic overrides, and be covered by a ContractTest redaction check (mirror
/// <c>RabbitMqTransportOptions.PasswordOverride</c> — SEC-02/SEC-06).
/// </para>
/// </remarks>
internal sealed class KafkaTransportOptions
{
    // ── Producer options ──────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the bootstrap server(s) used to establish the initial Kafka connection.
    /// Format: <c>host1:port1,host2:port2</c>. Must not be null or empty.
    /// </summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether idempotent producer mode is enabled.
    /// When <see langword="true"/>, the producer ensures exactly-once delivery of each message
    /// within a single producer session. Requires <see cref="Acks"/> = <see cref="Acks.All"/>
    /// and <see cref="MaxInFlight"/> &lt;= 5.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableIdempotence { get; set; } = true;

    /// <summary>
    /// Gets or sets the acknowledgement level required before a produce request is considered complete.
    /// Defaults to <see cref="Acks.All"/> (all in-sync replicas must acknowledge).
    /// </summary>
    public Acks Acks { get; set; } = Acks.All;

    /// <summary>
    /// Gets or sets the maximum number of retries for a failing produce request.
    /// When <see langword="null"/>, the librdkafka default is used (effectively unlimited retries).
    /// Set explicitly to constrain retry behaviour.
    /// </summary>
    public int? MessageSendMaxRetries { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of in-flight produce requests per broker connection.
    /// Must be &lt;= 5 when <see cref="EnableIdempotence"/> is <see langword="true"/>.
    /// Defaults to <c>5</c>.
    /// </summary>
    public int MaxInFlight { get; set; } = 5;

    /// <summary>
    /// Gets or sets the delay in milliseconds to wait for messages in the producer queue to
    /// accumulate before constructing message batches (<c>linger.ms</c>).
    /// When <see langword="null"/>, the librdkafka default is used.
    /// </summary>
    public int? LingerMs { get; set; }

    /// <summary>
    /// Gets or sets the maximum size in bytes of all messages batched in one MessageSet
    /// (<c>batch.size</c>). When <see langword="null"/>, the librdkafka default is used.
    /// </summary>
    public int? BatchSize { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages allowed on the producer queue
    /// (<c>queue.buffering.max.messages</c>). When <see langword="null"/>, the librdkafka
    /// default (100 000) is used. Explicit bounded values align with ADR-004/ADR-006.
    /// </summary>
    public int? QueueBufferingMaxMessages { get; set; }

    /// <summary>
    /// Gets or sets the maximum total size in kilobytes of all messages on the producer queue
    /// (<c>queue.buffering.max.kbytes</c>). When <see langword="null"/>, the librdkafka
    /// default is used. Explicit bounded values align with ADR-004/ADR-006.
    /// </summary>
    public int? QueueBufferingMaxKbytes { get; set; }

    /// <summary>
    /// Gets or sets the maximum time to wait for unflushed messages to be delivered during
    /// <see cref="IAsyncDisposable.DisposeAsync"/>. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // ── Consumer options ──────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the consumer group identifier. Required when using
    /// <c>ITransportAdapter.ConsumeAsync</c>.
    /// All consumers sharing the same <see cref="GroupId"/> coordinate partition assignment and
    /// offset commits via the Kafka group coordinator.
    /// </summary>
    /// <remarks>
    /// <b>Not required for producer-only usage.</b> Validation is enforced in
    /// <see cref="ValidateConsumer"/> (called from <c>ConsumeAsync</c>) rather than
    /// <see cref="Validate"/> (called from the constructor), so producer-only DI registrations
    /// are not forced to supply a group id.
    /// </remarks>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the offset reset policy applied when a consumer group has no committed offset
    /// for a partition, or when the committed offset is out of range.
    /// Defaults to <see cref="Confluent.Kafka.AutoOffsetReset.Earliest"/> (start from the
    /// beginning of the log).
    /// </summary>
    public AutoOffsetReset AutoOffsetReset { get; set; } = AutoOffsetReset.Earliest;

    /// <summary>
    /// Gets or sets the partition assignment strategy applied by the consumer group coordinator.
    /// Defaults to <see cref="KafkaPartitionAssignmentStrategy.CooperativeSticky"/> (D9),
    /// which performs incremental rebalancing and minimises stop-the-world pauses.
    /// </summary>
    public KafkaPartitionAssignmentStrategy ConsumerPartitionAssignmentStrategy { get; set; } =
        KafkaPartitionAssignmentStrategy.CooperativeSticky;

    /// <summary>
    /// Gets or sets a value indicating whether librdkafka should periodically commit offsets
    /// to the broker in the background (<c>enable.auto.commit</c>).
    /// Defaults to <see langword="true"/> — combined with <see cref="EnableAutoOffsetStore"/> =
    /// <see langword="false"/> this achieves at-least-once delivery: offsets are only stored
    /// (and therefore committed) after successful <c>SettleAsync(Ack, …)</c> (D6).
    /// </summary>
    public bool EnableAutoCommit { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether librdkafka should automatically store the offset
    /// of the last message returned by <c>Consume()</c> before the message is processed
    /// (<c>enable.auto.offset.store</c>).
    /// Defaults to <see langword="false"/> so that offsets are stored manually via
    /// <c>IConsumer.StoreOffset(…)</c> inside <c>SettleAsync(Ack, …)</c> only (D6).
    /// </summary>
    public bool EnableAutoOffsetStore { get; set; }

    /// <summary>
    /// Gets or sets the consumer group session timeout in milliseconds.
    /// When <see langword="null"/>, the librdkafka default (45 000 ms) is used.
    /// </summary>
    public int? SessionTimeoutMs { get; set; }

    /// <summary>
    /// Gets or sets the maximum interval in milliseconds between two consecutive poll calls
    /// before the broker considers the consumer dead and triggers a rebalance
    /// (<c>max.poll.interval.ms</c>).
    /// When <see langword="null"/>, the librdkafka default (300 000 ms) is used.
    /// Set this to a value larger than the maximum message processing latency to avoid
    /// spurious rebalances when back-pressure slows the poll loop (D3).
    /// </summary>
    public int? MaxPollIntervalMs { get; set; }

    // ── Retry-topic + DLQ-topic pattern (R1.3) ─────────────────────────────────

    /// <summary>
    /// Gets or sets the emulated retry-topic + DLQ-topic configuration (R1.3, ADR-010).
    /// Kafka has no native DLQ; this drives republication of failed messages to a retry-topic
    /// (with exponential backoff) or a DLQ-topic (on rejection / retry exhaustion).
    /// Defaults to a disabled instance (<see cref="KafkaRetryDlqOptions.Enabled"/> = <see langword="false"/>)
    /// so producer-only and R1.2-style consumer usage are unaffected (opt-in).
    /// </summary>
    public KafkaRetryDlqOptions RetryDlq { get; set; } = new();

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates this options instance for producer usage, throwing
    /// <see cref="BareWireConfigurationException"/> when required values are missing or invalid.
    /// </summary>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <see cref="BootstrapServers"/> is null or empty.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(BootstrapServers))
        {
            throw new BareWireConfigurationException(
                optionName: nameof(BootstrapServers),
                optionValue: BootstrapServers,
                expectedValue: "A non-empty Kafka bootstrap server list (e.g. localhost:9092)");
        }
    }

    /// <summary>
    /// Validates consumer-specific options, throwing <see cref="BareWireConfigurationException"/>
    /// when <see cref="GroupId"/> is null or empty. Called from <c>ConsumeAsync</c> before
    /// building the <c>IConsumer</c> instance.
    /// </summary>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <see cref="GroupId"/> is null or empty.
    /// </exception>
    internal void ValidateConsumer()
    {
        if (string.IsNullOrEmpty(GroupId))
        {
            throw new BareWireConfigurationException(
                optionName: nameof(GroupId),
                optionValue: GroupId,
                expectedValue: "A non-empty consumer group id (e.g. my-service-group)");
        }
    }
}
