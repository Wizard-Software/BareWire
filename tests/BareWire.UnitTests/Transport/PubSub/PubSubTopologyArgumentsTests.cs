using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.Google.PubSub.Topology;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubTopologyArgumentsTests
{
    // ── Defaults (no arguments) ───────────────────────────────────────────────

    [Fact]
    public void Parse_NoArguments_ReturnsDefaults()
    {
        var queue = new QueueDeclaration("my-subscription");

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.AckDeadline.Should().Be(TimeSpan.FromSeconds(60));
        spec.OrderingEnabled.Should().BeFalse();
        spec.DeadLetterTopic.Should().BeNull();
        spec.MaxDeliveryAttempts.Should().Be(5);
        spec.MaxOutstandingMessages.Should().Be(1_000);
        spec.MaxOutstandingBytes.Should().Be(64L * 1024 * 1024);
    }

    [Fact]
    public void Parse_NullArguments_ReturnsDefaults()
    {
        var queue = new QueueDeclaration("my-subscription", Arguments: null);

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.AckDeadline.Should().Be(TimeSpan.FromSeconds(60));
    }

    // ── Parsing bw.pubsub.* keys ──────────────────────────────────────────────

    [Fact]
    public void Parse_AckDeadlineTimeSpanString_ParsesCorrectly()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.AckDeadlineKey] = "00:02:00",  // 120 seconds
        });

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.AckDeadline.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Parse_AckDeadlineAsTimeSpanObject_ParsesCorrectly()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.AckDeadlineKey] = TimeSpan.FromSeconds(30),
        });

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.AckDeadline.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Parse_OrderingEnabledTrue_ParsesCorrectly()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.OrderingEnabledKey] = true,
        });

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.OrderingEnabled.Should().BeTrue();
    }

    [Fact]
    public void Parse_DeadLetterTopic_ParsesCorrectly()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.DeadLetterTopicKey] = "projects/my-project/topics/dlq",
        });

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.DeadLetterTopic.Should().Be("projects/my-project/topics/dlq");
    }

    [Fact]
    public void Parse_MaxDeliveryAttempts_ParsesCorrectly()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.MaxDeliveryAttemptsKey] = 10,
        });

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.MaxDeliveryAttempts.Should().Be(10);
    }

    [Fact]
    public void Parse_MaxOutstandingMessages_ParsesCorrectly()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.MaxOutstandingMessagesKey] = 500,
        });

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.MaxOutstandingMessages.Should().Be(500);
    }

    [Fact]
    public void Parse_MaxOutstandingBytes_ParsesCorrectly()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.MaxOutstandingBytesKey] = 32L * 1024 * 1024,
        });

        PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

        spec.MaxOutstandingBytes.Should().Be(32L * 1024 * 1024);
    }

    [Fact]
    public void Parse_UnknownBwPubsubKey_IsIgnoredSilently()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            ["bw.pubsub.future-feature"] = "some-value",
        });

        // Must not throw — unknown keys are silently ignored (forward-compatible).
        Action act = () => PubSubTopologyArguments.Parse(queue);

        act.Should().NotThrow();
    }

    // ── Bad values → BareWireConfigurationException ───────────────────────────

    [Fact]
    public void Parse_InvalidAckDeadlineString_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.AckDeadlineKey] = "not-a-timespan",
        });

        Action act = () => PubSubTopologyArguments.Parse(queue);

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage($"*{PubSubTopologyArguments.AckDeadlineKey}*");
    }

    [Fact]
    public void Parse_AckDeadlineBelowMinimum_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.AckDeadlineKey] = TimeSpan.FromSeconds(5),  // below 10 s
        });

        Action act = () => PubSubTopologyArguments.Parse(queue);

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage($"*{PubSubTopologyArguments.AckDeadlineKey}*");
    }

    [Fact]
    public void Parse_MaxDeliveryAttemptsBelowMinimum_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.MaxDeliveryAttemptsKey] = 4,  // below Pub/Sub minimum of 5
        });

        Action act = () => PubSubTopologyArguments.Parse(queue);

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage($"*{PubSubTopologyArguments.MaxDeliveryAttemptsKey}*");
    }

    [Fact]
    public void Parse_InvalidOrderingEnabledValue_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.OrderingEnabledKey] = "not-a-bool",
        });

        Action act = () => PubSubTopologyArguments.Parse(queue);

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage($"*{PubSubTopologyArguments.OrderingEnabledKey}*");
    }

    [Fact]
    public void Parse_MaxOutstandingBytesZero_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("sub", Arguments: new Dictionary<string, object>
        {
            [PubSubTopologyArguments.MaxOutstandingBytesKey] = 0L,
        });

        Action act = () => PubSubTopologyArguments.Parse(queue);

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage($"*{PubSubTopologyArguments.MaxOutstandingBytesKey}*");
    }
}
