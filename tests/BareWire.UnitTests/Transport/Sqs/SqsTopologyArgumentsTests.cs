using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.AWS.SQS.Topology;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

public sealed class SqsTopologyArgumentsTests
{
    // ── Default values ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NoArguments_ReturnsDefaults()
    {
        var queue = new QueueDeclaration("test-queue");

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.VisibilityTimeout.Should().Be(TimeSpan.FromSeconds(30));
        spec.WaitTimeSeconds.Should().Be(20);
        spec.IsFifo.Should().BeFalse();
        spec.MaxReceiveCount.Should().Be(5);
    }

    [Fact]
    public void Parse_NullArguments_ReturnsDefaults()
    {
        var queue = new QueueDeclaration("test-queue", Arguments: null);

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.VisibilityTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    // ── bw.sqs.visibility-timeout ─────────────────────────────────────────────

    [Fact]
    public void Parse_VisibilityTimeoutTimeSpan_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.VisibilityTimeout] = TimeSpan.FromSeconds(60),
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.VisibilityTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Parse_VisibilityTimeoutString_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.VisibilityTimeout] = "00:01:30",
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.VisibilityTimeout.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Parse_VisibilityTimeoutInvalidString_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.VisibilityTimeout] = "not-a-timespan",
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(SqsTopologyArguments.VisibilityTimeout);
    }

    // ── bw.sqs.wait-time-seconds ──────────────────────────────────────────────

    [Fact]
    public void Parse_WaitTimeSeconds_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.WaitTimeSecondsKey] = 15,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.WaitTimeSeconds.Should().Be(15);
    }

    [Fact]
    public void Parse_WaitTimeSecondsOutOfRange_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.WaitTimeSecondsKey] = 25,
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(SqsTopologyArguments.WaitTimeSecondsKey);
    }

    // ── bw.sqs.fifo ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_FifoTrue_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q.fifo", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.FifoKey] = true,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.IsFifo.Should().BeTrue();
    }

    [Fact]
    public void Parse_FifoStringTrue_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q.fifo", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.FifoKey] = "true",
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.IsFifo.Should().BeTrue();
    }

    [Fact]
    public void Parse_FifoInvalidValue_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("q.fifo", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.FifoKey] = "yes",
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(SqsTopologyArguments.FifoKey);
    }

    // ── bw.sqs.max-receive-count ──────────────────────────────────────────────

    [Fact]
    public void Parse_MaxReceiveCount_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.MaxReceiveCountKey] = 10,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.MaxReceiveCount.Should().Be(10);
    }

    [Fact]
    public void Parse_MaxReceiveCountZero_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.MaxReceiveCountKey] = 0,
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(SqsTopologyArguments.MaxReceiveCountKey);
    }

    // ── Unknown keys ignored ──────────────────────────────────────────────────

    [Fact]
    public void Parse_UnknownKey_IsIgnoredAndDefaultsApplied()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            ["bw.sqs.unknown-future-key"] = "whatever",
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        // Unknown keys silently ignored; defaults still applied.
        spec.WaitTimeSeconds.Should().Be(20);
    }
}
