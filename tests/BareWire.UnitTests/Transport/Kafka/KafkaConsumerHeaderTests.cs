using AwesomeAssertions;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;

namespace BareWire.UnitTests.Transport.Kafka;

/// <summary>
/// Unit tests for the pure header-merge logic in <see cref="KafkaConsumer.MergeHeaders"/>.
/// These tests do not require a live broker (R1.5 covers full consume/commit/rebalance).
/// </summary>
public sealed class KafkaConsumerHeaderTests
{
    // ── D5: Last-write-wins override (anti-spoofing) ──────────────────────────

    [Fact]
    public void MergeHeaders_WireConsumerIdPresent_IsOverwrittenByRealConsumerId()
    {
        // Arrange — attacker puts a spoofed BW-ConsumerId on the wire message.
        // MapInbound will copy it verbatim; MergeHeaders must overwrite it with the
        // real consumer id injected AFTER mapping (last-write-wins, D5 / SEC-1).
        var wireHeaders = new Headers();
        wireHeaders.Add("BW-ConsumerId", System.Text.Encoding.UTF8.GetBytes("attacker-consumer-id"));
        wireHeaders.Add("BW-Topic", System.Text.Encoding.UTF8.GetBytes("attacker-topic"));

        const string realConsumerId = "legitimate-consumer-abc123";
        const string topic = "real-topic";
        const int partition = 2;

        // Act
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            wireHeaders,
            topic: topic,
            partition: partition,
            consumerId: realConsumerId);

        // Assert — wire-level values MUST be overwritten
        merged["BW-ConsumerId"].Should().Be(realConsumerId,
            "BW-ConsumerId must be the real consumer id, not the wire-supplied value (D5 anti-spoofing)");

        merged["BW-Topic"].Should().Be(topic,
            "BW-Topic must be the real topic, not the wire-supplied value");
    }

    [Fact]
    public void MergeHeaders_NullKafkaHeaders_StillStampsBwHeaders()
    {
        // Arrange — message with no headers at all
        const string consumerId = "consumer-xyz";
        const string topic = "my-topic";
        const int partition = 0;

        // Act
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            kafkaHeaders: null,
            topic: topic,
            partition: partition,
            consumerId: consumerId);

        // Assert
        merged["BW-ConsumerId"].Should().Be(consumerId);
        merged["BW-Topic"].Should().Be(topic);
        merged["BW-Partition"].Should().Be("0");
    }

    [Fact]
    public void MergeHeaders_UserHeadersArePreserved()
    {
        // Arrange — message carries application-level headers alongside spoofed BW-* ones
        var wireHeaders = new Headers();
        wireHeaders.Add("x-correlation-id", System.Text.Encoding.UTF8.GetBytes("corr-42"));
        wireHeaders.Add("content-type", System.Text.Encoding.UTF8.GetBytes("application/json"));

        // Act
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            wireHeaders,
            topic: "t",
            partition: 1,
            consumerId: "c-1");

        // Assert — application headers remain intact
        merged["x-correlation-id"].Should().Be("corr-42");
        merged["content-type"].Should().Be("application/json");

        // And BW-* are injected
        merged["BW-ConsumerId"].Should().Be("c-1");
        merged["BW-Partition"].Should().Be("1");
    }

    [Fact]
    public void MergeHeaders_PartitionIsFormattedInvariantCulture()
    {
        // Arrange & Act
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            kafkaHeaders: null,
            topic: "t",
            partition: 12,
            consumerId: "c");

        // Assert — must be numeric string regardless of locale
        merged["BW-Partition"].Should().Be("12");
    }

    // ── Spec-required exact test names (C6 / SEC-1 / D5) ─────────────────────

    /// <summary>
    /// Required by spec C6: null wire headers path stamps BW-* headers.
    /// </summary>
    [Fact]
    public void MergeHeaders_NullWireHeaders_StampsBwHeaders()
    {
        // Arrange & Act
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            kafkaHeaders: null,
            topic: "my-topic",
            partition: 7,
            consumerId: "real-consumer");

        // Assert
        merged.Should().ContainKey("BW-Topic");
        merged.Should().ContainKey("BW-Partition");
        merged.Should().ContainKey("BW-ConsumerId");
        merged["BW-Topic"].Should().Be("my-topic");
        merged["BW-Partition"].Should().Be("7");
        merged["BW-ConsumerId"].Should().Be("real-consumer");
    }

    /// <summary>
    /// Required by spec C6: user-supplied wire headers (e.g. content-type) are preserved.
    /// </summary>
    [Fact]
    public void MergeHeaders_WireHeadersIncludeContentType_ArePreserved()
    {
        // Arrange
        var wireHeaders = new Headers();
        wireHeaders.Add("content-type", System.Text.Encoding.UTF8.GetBytes("application/json"));

        // Act
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            wireHeaders,
            topic: "t",
            partition: 0,
            consumerId: "c");

        // Assert — content-type survives the merge
        merged.Should().ContainKey("content-type");
        merged["content-type"].Should().Be("application/json");
    }

    /// <summary>
    /// Required by spec C6 SEC-1 / D5: spoofed BW-ConsumerId on the wire must be overwritten.
    /// Verifies that <see cref="KafkaConsumer.MergeHeaders"/> stamps authoritative values
    /// AFTER <see cref="KafkaHeaderMapper.MapInbound"/> (last-write-wins).
    /// </summary>
    [Fact]
    public void MergeHeaders_SpoofedBwConsumerIdWireHeader_IsOverwritten()
    {
        // Arrange — attacker injects BW-ConsumerId on the wire
        var wireHeaders = new Headers();
        wireHeaders.Add("BW-ConsumerId", System.Text.Encoding.UTF8.GetBytes("attacker"));

        // Act
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            wireHeaders,
            topic: "t",
            partition: 7,
            consumerId: "real-consumer");

        // Assert — attacker value must be overwritten (D5 last-write-wins)
        merged["BW-ConsumerId"].Should().Be("real-consumer",
            "wire-level BW-ConsumerId must be overwritten by the authoritative consumer id (SEC-1 / D5)");

        merged["BW-Partition"].Should().Be("7");
    }

    // ── SEC-1 (R1.3): strip retry/DLQ tracking headers on source-topic consumption ──

    [Fact]
    public void MergeHeaders_SourceTopic_StripsSpoofedRetryDlqTrackingHeaders()
    {
        // Arrange — a producer to the SOURCE topic spoofs the library's retry/DLQ tracking headers.
        // On source-topic consumption (isRetryOrDlqTopic: false) these must be stripped so they
        // cannot influence routing (BW-RetryCount) or mislead provenance/dead-letter metadata.
        var wireHeaders = new Headers();
        wireHeaders.Add("BW-RetryCount", System.Text.Encoding.UTF8.GetBytes("999"));
        wireHeaders.Add("BW-RetryAt", System.Text.Encoding.UTF8.GetBytes("2020-01-01T00:00:00Z"));
        wireHeaders.Add("BW-DeadLettered", System.Text.Encoding.UTF8.GetBytes("true"));
        wireHeaders.Add("BW-DeadLetterReason", System.Text.Encoding.UTF8.GetBytes("rejected"));
        wireHeaders.Add("BW-OriginalTopic", System.Text.Encoding.UTF8.GetBytes("victim-topic"));
        wireHeaders.Add("user-header", System.Text.Encoding.UTF8.GetBytes("keep-me"));

        // Act — consumed from a source topic
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            wireHeaders, topic: "orders", partition: 0, consumerId: "c-1", isRetryOrDlqTopic: false);

        // Assert — all five retry/DLQ tracking headers stripped; user headers + BW routing preserved
        merged.Should().NotContainKey("BW-RetryCount");
        merged.Should().NotContainKey("BW-RetryAt");
        merged.Should().NotContainKey("BW-DeadLettered");
        merged.Should().NotContainKey("BW-DeadLetterReason");
        merged.Should().NotContainKey("BW-OriginalTopic");
        merged["user-header"].Should().Be("keep-me");
        merged["BW-Topic"].Should().Be("orders");
        merged["BW-ConsumerId"].Should().Be("c-1");
    }

    [Fact]
    public void MergeHeaders_RetryDlqTopic_PreservesRetryDlqTrackingHeaders()
    {
        // Arrange — on a RETRY/DLQ topic the tracking headers were stamped by the library's own
        // republication producer and are legitimate; they must be preserved.
        var wireHeaders = new Headers();
        wireHeaders.Add("BW-RetryCount", System.Text.Encoding.UTF8.GetBytes("2"));
        wireHeaders.Add("BW-OriginalTopic", System.Text.Encoding.UTF8.GetBytes("orders"));

        // Act — consumed from a retry topic
        Dictionary<string, string> merged = KafkaConsumer.MergeHeaders(
            wireHeaders, topic: "orders.retry", partition: 0, consumerId: "c-1", isRetryOrDlqTopic: true);

        // Assert — library-stamped tracking headers survive on the retry/DLQ topic
        merged["BW-RetryCount"].Should().Be("2");
        merged["BW-OriginalTopic"].Should().Be("orders");
    }
}
