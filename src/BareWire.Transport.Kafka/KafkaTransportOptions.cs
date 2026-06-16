using BareWire.Abstractions.Exceptions;
using Confluent.Kafka;

namespace BareWire.Transport.Kafka;

/// <summary>
/// Configuration options for the Kafka transport adapter.
/// Apply via <see cref="ServiceCollectionExtensions.AddBareWireKafka"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope note (R1.1):</b> This class covers producer-side configuration only.
/// SASL/SSL (<c>SecurityProtocol</c>, SCRAM, OAUTHBEARER) are deferred to a dedicated security task
/// (R1.x). The producer defaults to <c>SecurityProtocol=Plaintext</c> — <b>do not use against a
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

    /// <summary>
    /// Validates this options instance, throwing <see cref="BareWireConfigurationException"/>
    /// when required values are missing or invalid.
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
}
