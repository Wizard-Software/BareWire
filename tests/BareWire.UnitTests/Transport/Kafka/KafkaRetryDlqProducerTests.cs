using System.Buffers;
using System.Globalization;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka.Internal;
using NSubstitute;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaRetryDlqProducerTests
{
    private static KafkaRetryDlqOptions EnabledOptions() => new()
    {
        Enabled = true,
        MaxRetryCount = 3,
        BaseDelay = TimeSpan.FromSeconds(1),
        BackoffMultiplier = 2.0,
        MaxDelay = TimeSpan.FromMinutes(5),
    };

    private static InboundMessage MessageWith(
        IReadOnlyDictionary<string, string> headers, byte[]? body = null)
    {
        body ??= [1, 2, 3];
        return new InboundMessage(
            messageId: "msg-1",
            headers: headers,
            body: new ReadOnlySequence<byte>(body),
            deliveryTag: 42UL);
    }

    // ── Capturing publisher ─────────────────────────────────────────────────────

    private static (KafkaRetryDlqProducer producer, IRetryDlqPublisher publisher, List<OutboundMessage> captured)
        CreateProducer()
    {
        var captured = new List<OutboundMessage>();
        var publisher = Substitute.For<IRetryDlqPublisher>();
        publisher.PublishAsync(Arg.Do<OutboundMessage>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return (new KafkaRetryDlqProducer(publisher), publisher, captured);
    }

    // ── Reject → DLQ ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RepublishToDlqAsync_RoutesToDlqTopic_WithReasonHeaders()
    {
        // Arrange
        (KafkaRetryDlqProducer producer, IRetryDlqPublisher publisher, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>());

        // Act
        await producer.RepublishToDlqAsync(
            message, sourceTopic: "orders",
            reason: KafkaRetryDlqProducer.DeadLetterReason.Rejected,
            EnabledOptions(), CancellationToken.None);

        // Assert
        await publisher.Received(1).PublishAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
        OutboundMessage outbound = captured.Single();
        outbound.RoutingKey.Should().Be("orders.DLQ");
        outbound.Headers[KafkaRetryDlqProducer.DeadLetteredHeader].Should().Be("true");
        outbound.Headers[KafkaRetryDlqProducer.DeadLetterReasonHeader]
            .Should().Be(KafkaRetryDlqProducer.DeadLetterReason.Rejected);
        outbound.Headers[KafkaRetryDlqProducer.OriginalTopicHeader].Should().Be("orders");
    }

    [Fact]
    public async Task RepublishToDlqAsync_PreservesBody()
    {
        // Arrange
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        byte[] payload = [10, 20, 30, 40];
        InboundMessage message = MessageWith(new Dictionary<string, string>(), payload);

        // Act
        await producer.RepublishToDlqAsync(
            message, "orders", KafkaRetryDlqProducer.DeadLetterReason.Rejected, EnabledOptions(), CancellationToken.None);

        // Assert
        captured.Single().Body.ToArray().Should().Equal(payload);
    }

    // ── Defer → retry ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RepublishToRetryAsync_RoutesToRetryTopic_AndIncrementsRetryCount()
    {
        // Arrange
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>());

        // Act — clamped current retry count = 0 → republished with BW-RetryCount = 1
        await producer.RepublishToRetryAsync(
            message, sourceTopic: "orders", clampedRetryCount: 0, EnabledOptions(), CancellationToken.None);

        // Assert
        OutboundMessage outbound = captured.Single();
        outbound.RoutingKey.Should().Be("orders.retry");
        outbound.Headers[KafkaRetryDlqProducer.RetryCountHeader].Should().Be("1");
        outbound.Headers.Should().ContainKey(KafkaRetryDlqProducer.RetryAtHeader);
        // BW-RetryAt is a parseable ISO-8601 UTC instant in the future.
        DateTimeOffset.TryParse(
            outbound.Headers[KafkaRetryDlqProducer.RetryAtHeader],
            CultureInfo.InvariantCulture, out DateTimeOffset retryAt).Should().BeTrue();
        retryAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RepublishToRetryAsync_IncrementsExistingRetryCount()
    {
        // Arrange — clamped current count 2 → republished with 3
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(
            new Dictionary<string, string> { [KafkaRetryDlqProducer.RetryCountHeader] = "2" });

        // Act
        await producer.RepublishToRetryAsync(message, "orders", clampedRetryCount: 2, EnabledOptions(), CancellationToken.None);

        // Assert
        captured.Single().Headers[KafkaRetryDlqProducer.RetryCountHeader].Should().Be("3");
    }

    // ── BW-OriginalTopic preserved across successive republications ──────────────

    [Fact]
    public async Task RepublishToRetryAsync_PreservesExistingOriginalTopic()
    {
        // Arrange — message already carries BW-OriginalTopic from a prior republication
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>
        {
            [KafkaRetryDlqProducer.OriginalTopicHeader] = "orders",
            [KafkaRetryDlqProducer.RetryCountHeader] = "1",
        });

        // Act — republish from the retry-topic (source = orders.retry), original must stay "orders"
        await producer.RepublishToRetryAsync(message, "orders.retry", clampedRetryCount: 1, EnabledOptions(), CancellationToken.None);

        // Assert
        captured.Single().Headers[KafkaRetryDlqProducer.OriginalTopicHeader].Should().Be("orders");
    }

    // ── ContentType via BW-ContentType header, with fallback ─────────────────────

    [Fact]
    public async Task RepublishToDlqAsync_UsesBwContentTypeHeader_WhenPresent()
    {
        // Arrange
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>
        {
            [KafkaRetryDlqProducer.ContentTypeHeader] = "application/json",
        });

        // Act
        await producer.RepublishToDlqAsync(message, "orders", KafkaRetryDlqProducer.DeadLetterReason.Rejected, EnabledOptions(), CancellationToken.None);

        // Assert
        captured.Single().ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task RepublishToDlqAsync_FallsBackToOctetStream_WhenNoContentType()
    {
        // Arrange
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>());

        // Act
        await producer.RepublishToDlqAsync(message, "orders", KafkaRetryDlqProducer.DeadLetterReason.Rejected, EnabledOptions(), CancellationToken.None);

        // Assert — required OutboundMessage.ContentType is never null (SEC §10.3)
        captured.Single().ContentType.Should().Be("application/octet-stream");
    }

    // ── Source-delivery BW-* headers stripped on republish (R1.2 D5) ─────────────

    [Fact]
    public async Task RepublishToRetryAsync_StripsSourceDeliveryHeaders()
    {
        // Arrange — message carries the consumer-stamped source-delivery headers
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>
        {
            ["BW-ConsumerId"] = "consumer-xyz",
            ["BW-Topic"] = "orders",
            ["BW-Partition"] = "2",
            ["user-header"] = "keep-me",
        });

        // Act
        await producer.RepublishToRetryAsync(message, "orders", clampedRetryCount: 0, EnabledOptions(), CancellationToken.None);

        // Assert — source-delivery headers stripped; user headers preserved
        OutboundMessage outbound = captured.Single();
        outbound.Headers.Should().NotContainKey("BW-ConsumerId");
        outbound.Headers.Should().NotContainKey("BW-Topic");
        outbound.Headers.Should().NotContainKey("BW-Partition");
        outbound.Headers["user-header"].Should().Be("keep-me");
    }

    [Fact]
    public async Task RepublishToRetryAsync_PublisherCalledExactlyOnce()
    {
        // Arrange
        (KafkaRetryDlqProducer producer, IRetryDlqPublisher publisher, _) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>());

        // Act
        await producer.RepublishToRetryAsync(message, "orders", clampedRetryCount: 0, EnabledOptions(), CancellationToken.None);

        // Assert — exactly one republish, no duplicate produce
        await publisher.Received(1).PublishAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepublishToDlqAsync_NackExhaustedReason_SetsReasonHeader()
    {
        // Arrange
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>());

        // Act — Nack at the retry cap dead-letters with the nack-exhausted reason
        await producer.RepublishToDlqAsync(
            message, "orders",
            reason: KafkaRetryDlqProducer.DeadLetterReason.NackExhausted,
            EnabledOptions(), CancellationToken.None);

        // Assert
        captured.Single().Headers[KafkaRetryDlqProducer.DeadLetterReasonHeader]
            .Should().Be(KafkaRetryDlqProducer.DeadLetterReason.NackExhausted);
    }

    [Fact]
    public async Task RepublishToDlqAsync_StripsSpoofedDeadLetterHeaders_BeforeReStamp()
    {
        // Arrange — message carries a spoofed dead-letter reason; the producer must re-stamp it,
        // not propagate the wire value (SEC-1 — internal-only metadata).
        (KafkaRetryDlqProducer producer, _, List<OutboundMessage> captured) = CreateProducer();
        InboundMessage message = MessageWith(new Dictionary<string, string>
        {
            [KafkaRetryDlqProducer.DeadLetterReasonHeader] = "spoofed-reason",
            [KafkaRetryDlqProducer.DeadLetteredHeader] = "false",
        });

        // Act
        await producer.RepublishToDlqAsync(
            message, "orders", KafkaRetryDlqProducer.DeadLetterReason.Rejected, EnabledOptions(), CancellationToken.None);

        // Assert — authoritative internal values, not the spoofed ones
        OutboundMessage outbound = captured.Single();
        outbound.Headers[KafkaRetryDlqProducer.DeadLetterReasonHeader].Should().Be(KafkaRetryDlqProducer.DeadLetterReason.Rejected);
        outbound.Headers[KafkaRetryDlqProducer.DeadLetteredHeader].Should().Be("true");
    }

    [Fact]
    public void Constructor_NullPublisher_ThrowsArgumentNullException()
    {
        Action act = () => _ = new KafkaRetryDlqProducer(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("publisher");
    }
}
