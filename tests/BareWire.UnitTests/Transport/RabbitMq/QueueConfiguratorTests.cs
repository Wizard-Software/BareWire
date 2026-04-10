using AwesomeAssertions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ.Topology;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class QueueConfiguratorTests
{
    private static QueueConfigurator CreateConfigurator() => new();

    // ── DeadLetterExchange ────────────────────────────────────────────────────

    [Fact]
    public void DeadLetterExchange_ValidName_SetsArgument()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeadLetterExchange("dlx.orders");
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-dead-letter-exchange"].Should().Be("dlx.orders");
    }

    [Fact]
    public void DeadLetterExchange_EmptyName_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.DeadLetterExchange(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── DeadLetterRoutingKey ──────────────────────────────────────────────────

    [Fact]
    public void DeadLetterRoutingKey_ValidKey_SetsArgument()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeadLetterRoutingKey("orders.failed");
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-dead-letter-routing-key"].Should().Be("orders.failed");
    }

    // ── MessageTtl ────────────────────────────────────────────────────────────

    [Fact]
    public void MessageTtl_ValidTimeSpan_ConvertsToMilliseconds()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MessageTtl(TimeSpan.FromHours(24));
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-message-ttl"].Should().Be(86400000L);
    }

    [Fact]
    public void MessageTtl_ZeroTimeSpan_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.MessageTtl(TimeSpan.Zero);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MessageTtl_NegativeTimeSpan_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.MessageTtl(TimeSpan.FromSeconds(-1));

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MessageTtl_ExceedsIntMaxValueMs_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.MessageTtl(TimeSpan.FromMilliseconds((double)int.MaxValue + 1));

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── MaxLength ─────────────────────────────────────────────────────────────

    [Fact]
    public void MaxLength_ValidValue_SetsArgument()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MaxLength(10_000L);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-max-length"].Should().Be(10_000L);
    }

    [Fact]
    public void MaxLength_ZeroOrNegative_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.MaxLength(0);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── MaxLengthBytes ────────────────────────────────────────────────────────

    [Fact]
    public void MaxLengthBytes_ValidValue_SetsArgument()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MaxLengthBytes(1_048_576L);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-max-length-bytes"].Should().Be(1_048_576L);
    }

    [Fact]
    public void MaxLengthBytes_ZeroOrNegative_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.MaxLengthBytes(-1);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── SetQueueType ──────────────────────────────────────────────────────────

    [Fact]
    public void SetQueueType_Quorum_SetsQuorumString()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.SetQueueType(QueueType.Quorum);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-queue-type"].Should().Be("quorum");
    }

    [Fact]
    public void SetQueueType_Stream_SetsStreamString()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.SetQueueType(QueueType.Stream);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-queue-type"].Should().Be("stream");
    }

    [Fact]
    public void SetQueueType_Classic_SetsClassicString()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.SetQueueType(QueueType.Classic);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-queue-type"].Should().Be("classic");
    }

    // ── Overflow ──────────────────────────────────────────────────────────────

    [Fact]
    public void Overflow_RejectPublish_SetsRejectPublishString()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.Overflow(OverflowStrategy.RejectPublish);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-overflow"].Should().Be("reject-publish");
    }

    [Fact]
    public void Overflow_RejectPublishDlx_SetsRejectPublishDlxString()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.Overflow(OverflowStrategy.RejectPublishDlx);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-overflow"].Should().Be("reject-publish-dlx");
    }

    [Fact]
    public void Overflow_DropHead_SetsDropHeadString()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.Overflow(OverflowStrategy.DropHead);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-overflow"].Should().Be("drop-head");
    }

    // ── Argument ──────────────────────────────────────────────────────────────

    [Fact]
    public void Argument_ValidKeyValue_SetsArgument()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.Argument("x-max-priority", 10);
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().NotBeNull();
        result!["x-max-priority"].Should().Be(10);
    }

    [Fact]
    public void Argument_EmptyKey_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.Argument(string.Empty, "value");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Argument_NullValue_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.Argument("x-custom", null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Fluent chaining ───────────────────────────────────────────────────────

    [Fact]
    public void FluentChaining_ReturnsSameInstance()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        IQueueConfigurator afterDeadLetterExchange = sut.DeadLetterExchange("dlx");
        IQueueConfigurator afterDeadLetterRoutingKey = sut.DeadLetterRoutingKey("key");
        IQueueConfigurator afterMessageTtl = sut.MessageTtl(TimeSpan.FromMinutes(5));
        IQueueConfigurator afterMaxLength = sut.MaxLength(1000);
        IQueueConfigurator afterMaxLengthBytes = sut.MaxLengthBytes(1024);
        IQueueConfigurator afterSetQueueType = sut.SetQueueType(QueueType.Quorum);
        IQueueConfigurator afterOverflow = sut.Overflow(OverflowStrategy.RejectPublish);
        IQueueConfigurator afterArgument = sut.Argument("x-max-priority", 5);

        // Assert
        afterDeadLetterExchange.Should().BeSameAs(sut);
        afterDeadLetterRoutingKey.Should().BeSameAs(sut);
        afterMessageTtl.Should().BeSameAs(sut);
        afterMaxLength.Should().BeSameAs(sut);
        afterMaxLengthBytes.Should().BeSameAs(sut);
        afterSetQueueType.Should().BeSameAs(sut);
        afterOverflow.Should().BeSameAs(sut);
        afterArgument.Should().BeSameAs(sut);
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_NoArguments_ReturnsNull()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        IReadOnlyDictionary<string, object>? result = sut.Build();

        // Assert
        result.Should().BeNull();
    }
}
