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

    // ── bw.sqs.content-based-deduplication ───────────────────────────────────

    [Fact]
    public void Parse_ContentBasedDeduplicationTrue_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q.fifo", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.ContentBasedDeduplicationKey] = true,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.ContentBasedDeduplication.Should().BeTrue();
    }

    [Fact]
    public void Parse_ContentBasedDeduplicationStringTrue_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q.fifo", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.ContentBasedDeduplicationKey] = "true",
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.ContentBasedDeduplication.Should().BeTrue();
    }

    [Fact]
    public void Parse_ContentBasedDeduplicationFalse_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q.fifo", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.ContentBasedDeduplicationKey] = false,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.ContentBasedDeduplication.Should().BeFalse();
    }

    [Fact]
    public void Parse_ContentBasedDeduplicationInvalidValue_ThrowsBareWireConfigurationException()
    {
        var queue = new QueueDeclaration("q.fifo", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.ContentBasedDeduplicationKey] = "yes",
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(SqsTopologyArguments.ContentBasedDeduplicationKey);
    }

    [Fact]
    public void Parse_NoArguments_ContentBasedDeduplicationDefaultsFalse()
    {
        var queue = new QueueDeclaration("q.fifo");

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.ContentBasedDeduplication.Should().BeFalse();
    }

    // ── bw.sqs.sse-managed (SSE-SQS) — R4.3 ─────────────────────────────────

    [Fact]
    public void Parse_SseManaged_True_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.SseManagedKey] = true,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.SseManaged.Should().BeTrue();
    }

    [Fact]
    public void Parse_SseManaged_StringTrue_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.SseManagedKey] = "true",
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.SseManaged.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoArguments_SseManagedDefaultsFalse()
    {
        var queue = new QueueDeclaration("q");

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.SseManaged.Should().BeFalse();
    }

    // ── bw.sqs.kms-master-key-id (SSE-KMS) — R4.3 ───────────────────────────

    [Fact]
    public void Parse_KmsMasterKeyId_ParsedCorrectly()
    {
        const string keyArn = "arn:aws:kms:eu-central-1:123456789:key/abc-def";
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.KmsMasterKeyIdKey] = keyArn,
            [SqsTopologyArguments.KmsDataKeyReusePeriodSecondsKey] = 300,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.KmsMasterKeyId.Should().Be(keyArn);
        spec.KmsDataKeyReusePeriodSeconds.Should().Be(300);
    }

    [Fact]
    public void Parse_KmsDataKeyReusePeriod_AtMinBoundary_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.KmsMasterKeyIdKey] = "alias/aws/sqs",
            [SqsTopologyArguments.KmsDataKeyReusePeriodSecondsKey] = 60,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.KmsDataKeyReusePeriodSeconds.Should().Be(60);
    }

    [Fact]
    public void Parse_KmsDataKeyReusePeriod_AtMaxBoundary_ParsedCorrectly()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.KmsMasterKeyIdKey] = "alias/aws/sqs",
            [SqsTopologyArguments.KmsDataKeyReusePeriodSecondsKey] = 86400,
        });

        SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

        spec.KmsDataKeyReusePeriodSeconds.Should().Be(86400);
    }

    [Fact]
    public void Parse_KmsDataKeyReusePeriod_BelowMinimum_ThrowsConfigurationException()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.KmsMasterKeyIdKey] = "alias/aws/sqs",
            [SqsTopologyArguments.KmsDataKeyReusePeriodSecondsKey] = 59,
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(SqsTopologyArguments.KmsDataKeyReusePeriodSecondsKey);
    }

    [Fact]
    public void Parse_KmsDataKeyReusePeriod_AboveMaximum_ThrowsConfigurationException()
    {
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.KmsMasterKeyIdKey] = "alias/aws/sqs",
            [SqsTopologyArguments.KmsDataKeyReusePeriodSecondsKey] = 86401,
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(SqsTopologyArguments.KmsDataKeyReusePeriodSecondsKey);
    }

    // ── Mutual exclusion SSE-SQS vs SSE-KMS — R4.3 (SEC-1 order-independent) ─

    [Fact]
    public void Parse_SseManagedAndKmsKeyId_SseManagedFirst_ThrowsConfigurationException()
    {
        // SEC-1: test order-independence — sse-managed key appears first in dictionary.
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.SseManagedKey] = true,
            [SqsTopologyArguments.KmsMasterKeyIdKey] = "alias/aws/sqs",
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>(
            "SSE-SQS and SSE-KMS are mutually exclusive — SQS rejects both set simultaneously");
    }

    [Fact]
    public void Parse_SseManagedAndKmsKeyId_KmsFirst_ThrowsConfigurationException()
    {
        // SEC-1: test order-independence — kms key appears first in dictionary.
        var queue = new QueueDeclaration("q", Arguments: new Dictionary<string, object>
        {
            [SqsTopologyArguments.KmsMasterKeyIdKey] = "alias/aws/sqs",
            [SqsTopologyArguments.SseManagedKey] = true,
        });

        Action act = () => SqsTopologyArguments.Parse(queue);

        act.Should().Throw<BareWireConfigurationException>(
            "mutual exclusion must be enforced regardless of the argument order in the dictionary");
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
