using System.Buffers;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

public sealed class SqsTransportAdapterTests
{
    private static SqsTransportOptions DefaultOptions() => new()
    {
        AuthMode = SqsAuthMode.DefaultChain,
    };

    // ── TransportName ─────────────────────────────────────────────────────────

    [Fact]
    public void TransportName_IsAwsSqs()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        adapter.TransportName.Should().Be("AWS.SQS");
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_ContainsNativeDeduplication()
    {
        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            Substitute.For<IAmazonSQS>());

        adapter.Capabilities.HasFlag(TransportCapabilities.NativeDeduplication)
            .Should().BeTrue();
    }

    [Fact]
    public void Capabilities_ContainsDlqNative()
    {
        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            Substitute.For<IAmazonSQS>());

        adapter.Capabilities.HasFlag(TransportCapabilities.DlqNative)
            .Should().BeTrue();
    }

    [Fact]
    public void Capabilities_ContainsBatchReceive()
    {
        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            Substitute.For<IAmazonSQS>());

        adapter.Capabilities.HasFlag(TransportCapabilities.BatchReceive)
            .Should().BeTrue();
    }

    // ── BuildClient — InstanceProfile mode (R4.3) ────────────────────────────

    [Fact]
    public async Task SendBatchAsync_InstanceProfileMode_DoesNotThrowWhenClientPreInjected()
    {
        // Validates InstanceProfile branch by injecting a pre-built mock — EnsureClientAsync
        // skips BuildClient when _client is already set (test constructor). This proves the
        // auth mode path is reachable without contacting IMDS.
        var options = new SqsTransportOptions
        {
            AuthMode = SqsAuthMode.InstanceProfile,
            InstanceProfileRoleName = string.Empty,
        };

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse
                { QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/q" }));
        sqsClient.SendMessageBatchAsync(
                Arg.Any<SendMessageBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SendMessageBatchResponse { Successful = [], Failed = [] }));

        var adapter = new SqsTransportAdapter(options, NullLogger<SqsTransportAdapter>.Instance, sqsClient);

        Func<Task> act = async () => await adapter.SendBatchAsync([]);

        await act.Should().NotThrowAsync(
            "InstanceProfile mode with an injected client must not throw");
    }

    // ── SendBatchAsync — chunking ─────────────────────────────────────────────

    [Fact]
    public async Task SendBatchAsync_TwentyThreeMessages_CallsSendMessageBatchAsyncExactlyThreeTimes()
    {
        // Arrange — 23 messages all targeting the same queue → chunks: 10 + 10 + 3.
        const string queueUrl = "https://sqs.eu-central-1.amazonaws.com/123/my-queue";
        const string queueName = "my-queue";

        var sqsClient = Substitute.For<IAmazonSQS>();

        // GetQueueUrlAsync returns the mock URL.
        sqsClient.GetQueueUrlAsync(queueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = queueUrl }));

        // All 3 batch calls succeed with empty Successful/Failed lists
        // (we just need them not to throw).
        sqsClient.SendMessageBatchAsync(
                Arg.Any<SendMessageBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<SendMessageBatchRequest>();
                var successful = req.Entries.Select(e => new SendMessageBatchResultEntry
                {
                    Id = e.Id,
                    MessageId = $"msg-{e.Id}",
                }).ToList();
                return Task.FromResult(new SendMessageBatchResponse
                {
                    Successful = successful,
                    Failed = [],
                });
            });

        var options = DefaultOptions();
        var adapter = new SqsTransportAdapter(
            options,
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        var messages = Enumerable.Range(0, 23)
            .Select(i => new OutboundMessage(
                routingKey: queueName,
                headers: new Dictionary<string, string>(),
                body: ReadOnlyMemory<byte>.Empty,
                contentType: "application/json"))
            .ToList();

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(messages);

        // Assert — exactly 3 batch calls (10 + 10 + 3 = 23 messages).
        await sqsClient.Received(3).SendMessageBatchAsync(
            Arg.Any<SendMessageBatchRequest>(),
            Arg.Any<CancellationToken>());

        results.Should().HaveCount(23);
    }

    [Fact]
    public async Task SendBatchAsync_TenMessages_CallsSendMessageBatchAsyncExactlyOnce()
    {
        const string queueUrl = "https://sqs.eu-central-1.amazonaws.com/123/q";
        const string queueName = "q";

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(queueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = queueUrl }));
        sqsClient.SendMessageBatchAsync(
                Arg.Any<SendMessageBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<SendMessageBatchRequest>();
                return Task.FromResult(new SendMessageBatchResponse
                {
                    Successful = req.Entries.Select(e => new SendMessageBatchResultEntry
                    {
                        Id = e.Id,
                        MessageId = $"m-{e.Id}",
                    }).ToList(),
                    Failed = [],
                });
            });

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        var messages = Enumerable.Range(0, 10)
            .Select(_ => new OutboundMessage(queueName, new Dictionary<string, string>(),
                ReadOnlyMemory<byte>.Empty, "application/json"))
            .ToList();

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(messages);

        await sqsClient.Received(1).SendMessageBatchAsync(
            Arg.Any<SendMessageBatchRequest>(),
            Arg.Any<CancellationToken>());

        results.Should().HaveCount(10);
        results.All(r => r.IsConfirmed).Should().BeTrue();
    }

    [Fact]
    public async Task SendBatchAsync_EmptyList_ReturnsEmpty()
    {
        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            Substitute.For<IAmazonSQS>());

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([]);

        results.Should().BeEmpty();
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            Substitute.For<IAmazonSQS>());

        await adapter.DisposeAsync();

        Func<Task> act = async () => await adapter.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // ── DeployTopologyAsync — SSE attributes (R4.3) ───────────────────────────

    [Fact]
    public async Task DeployTopologyAsync_SseManagedTrue_SetsSqsManagedSseEnabledAttribute()
    {
        var topology = new BareWire.Abstractions.Topology.TopologyDeclaration
        {
            Queues =
            [
                new BareWire.Abstractions.Topology.QueueDeclaration(
                    "sse-queue",
                    Arguments: new Dictionary<string, object>
                    {
                        ["bw.sqs.sse-managed"] = true,
                    }),
            ],
        };

        CreateQueueRequest? captured = null;
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.CreateQueueAsync(
                Arg.Do<CreateQueueRequest>(r => captured = r),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateQueueResponse
                { QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/sse-queue" }));

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        await adapter.DeployTopologyAsync(topology);

        captured.Should().NotBeNull();
        captured!.Attributes.Should().ContainKey("SqsManagedSseEnabled",
            "SSE-SQS requires the SqsManagedSseEnabled attribute on the CreateQueueRequest");
        captured.Attributes["SqsManagedSseEnabled"].Should().Be("true");
        captured.Attributes.Should().NotContainKey("KmsMasterKeyId",
            "SSE-SQS and SSE-KMS are mutually exclusive — KmsMasterKeyId must not be set");
    }

    [Fact]
    public async Task DeployTopologyAsync_KmsMasterKeyId_SetsKmsMasterKeyIdAttribute()
    {
        const string keyArn = "arn:aws:kms:eu-central-1:123456789:key/abc-def";
        var topology = new BareWire.Abstractions.Topology.TopologyDeclaration
        {
            Queues =
            [
                new BareWire.Abstractions.Topology.QueueDeclaration(
                    "kms-queue",
                    Arguments: new Dictionary<string, object>
                    {
                        ["bw.sqs.kms-master-key-id"] = keyArn,
                        ["bw.sqs.kms-data-key-reuse-period"] = 300,
                    }),
            ],
        };

        CreateQueueRequest? captured = null;
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.CreateQueueAsync(
                Arg.Do<CreateQueueRequest>(r => captured = r),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateQueueResponse
                { QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/kms-queue" }));

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        await adapter.DeployTopologyAsync(topology);

        captured.Should().NotBeNull();
        captured!.Attributes.Should().ContainKey("KmsMasterKeyId",
            "SSE-KMS requires the KmsMasterKeyId attribute on the CreateQueueRequest");
        captured.Attributes["KmsMasterKeyId"].Should().Be(keyArn);
        captured.Attributes.Should().ContainKey("KmsDataKeyReusePeriodSeconds");
        captured.Attributes["KmsDataKeyReusePeriodSeconds"].Should().Be("300");
        captured.Attributes.Should().NotContainKey("SqsManagedSseEnabled",
            "SSE-KMS must not also set SqsManagedSseEnabled");
    }

    // ── SendBatchAsync — FIFO queues ──────────────────────────────────────────

    [Fact]
    public async Task SendBatchAsync_FifoQueueWithCorrelationIdHeader_SetsMessageGroupIdOnEntry()
    {
        // Arrange
        const string fifoQueueName = "my-orders.fifo";
        const string fifoQueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/my-orders.fifo";
        const string correlationId = "saga-correlation-abc";

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(fifoQueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = fifoQueueUrl }));

        SendMessageBatchRequest? captured = null;
        sqsClient.SendMessageBatchAsync(
                Arg.Any<SendMessageBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<SendMessageBatchRequest>();
                return Task.FromResult(new SendMessageBatchResponse
                {
                    Successful = captured.Entries.Select(e => new SendMessageBatchResultEntry
                    {
                        Id = e.Id,
                        MessageId = $"msg-{e.Id}",
                    }).ToList(),
                    Failed = [],
                });
            });

        var options = DefaultOptions();
        var adapter = new SqsTransportAdapter(
            options,
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        var message = new OutboundMessage(
            routingKey: fifoQueueName,
            headers: new Dictionary<string, string>
            {
                [SqsHeaderMapper.CorrelationIdHeader] = correlationId,
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/json");

        // Act
        await adapter.SendBatchAsync([message]);

        // Assert — MessageGroupId must be set to the correlation-id fallback value.
        captured.Should().NotBeNull();
        captured!.Entries.Should().HaveCount(1);
        captured.Entries[0].MessageGroupId.Should().Be(correlationId);
    }

    [Fact]
    public async Task SendBatchAsync_FifoQueueWithExplicitMessageGroupIdHeader_PrioritisesExplicitHeader()
    {
        const string fifoQueueName = "my-orders.fifo";
        const string fifoQueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/my-orders.fifo";

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(fifoQueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = fifoQueueUrl }));

        SendMessageBatchRequest? captured = null;
        sqsClient.SendMessageBatchAsync(
                Arg.Any<SendMessageBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<SendMessageBatchRequest>();
                return Task.FromResult(new SendMessageBatchResponse
                {
                    Successful = captured.Entries.Select(e => new SendMessageBatchResultEntry
                    {
                        Id = e.Id,
                        MessageId = $"msg-{e.Id}",
                    }).ToList(),
                    Failed = [],
                });
            });

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        var message = new OutboundMessage(
            routingKey: fifoQueueName,
            headers: new Dictionary<string, string>
            {
                [SqsHeaderMapper.MessageGroupIdHeader] = "explicit-group",
                [SqsHeaderMapper.CorrelationIdHeader] = "should-not-be-used",
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/json");

        await adapter.SendBatchAsync([message]);

        captured.Should().NotBeNull();
        captured!.Entries[0].MessageGroupId.Should().Be("explicit-group");
    }

    [Fact]
    public async Task SendBatchAsync_StandardQueue_DoesNotSetMessageGroupId()
    {
        // Standard queues (no .fifo suffix) must NOT have MessageGroupId set.
        const string standardQueueName = "my-standard-queue";
        const string standardQueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/my-standard-queue";

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(standardQueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = standardQueueUrl }));

        SendMessageBatchRequest? captured = null;
        sqsClient.SendMessageBatchAsync(
                Arg.Any<SendMessageBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<SendMessageBatchRequest>();
                return Task.FromResult(new SendMessageBatchResponse
                {
                    Successful = captured.Entries.Select(e => new SendMessageBatchResultEntry
                    {
                        Id = e.Id,
                        MessageId = $"msg-{e.Id}",
                    }).ToList(),
                    Failed = [],
                });
            });

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        var message = new OutboundMessage(
            routingKey: standardQueueName,
            headers: new Dictionary<string, string>
            {
                [SqsHeaderMapper.CorrelationIdHeader] = "some-corr-id",
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/json");

        await adapter.SendBatchAsync([message]);

        captured.Should().NotBeNull();
        captured!.Entries[0].MessageGroupId.Should().BeNull(
            "standard queues must not have MessageGroupId — SQS returns InvalidParameterValue");
    }

    [Fact]
    public async Task SendBatchAsync_FifoQueueWithNoGroupOrCorrelationHeader_ThrowsBareWireTransportException()
    {
        const string fifoQueueName = "my-orders.fifo";
        const string fifoQueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/my-orders.fifo";

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(fifoQueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = fifoQueueUrl }));

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        // No BW-MessageGroupId and no correlation-id headers.
        var message = new OutboundMessage(
            routingKey: fifoQueueName,
            headers: new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/json");

        Func<Task> act = async () => await adapter.SendBatchAsync([message]);

        BareWireTransportException ex = (await act.Should().ThrowAsync<BareWireTransportException>()).Which;

        // SEC: exception message must contain the queue name and header NAMES but NOT header VALUES.
        ex.Message.Should().Contain(fifoQueueName);
        ex.Message.Should().Contain(SqsHeaderMapper.MessageGroupIdHeader);
        ex.Message.Should().Contain(SqsHeaderMapper.CorrelationIdHeader);
        // The "application/json" content-type value must not appear in the message.
        ex.Message.Should().NotContain("application/json");
    }

    [Fact]
    public async Task SendBatchAsync_FifoQueueWithContentBasedDeduplicationEnabled_DoesNotSetMessageDeduplicationId()
    {
        const string fifoQueueName = "my-orders.fifo";
        const string fifoQueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/my-orders.fifo";

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(fifoQueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = fifoQueueUrl }));

        SendMessageBatchRequest? captured = null;
        sqsClient.SendMessageBatchAsync(
                Arg.Any<SendMessageBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<SendMessageBatchRequest>();
                return Task.FromResult(new SendMessageBatchResponse
                {
                    Successful = captured.Entries.Select(e => new SendMessageBatchResultEntry
                    {
                        Id = e.Id,
                        MessageId = $"msg-{e.Id}",
                    }).ToList(),
                    Failed = [],
                });
            });

        var options = DefaultOptions();
        options.EnableContentBasedDeduplication = true;

        var adapter = new SqsTransportAdapter(
            options,
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        var message = new OutboundMessage(
            routingKey: fifoQueueName,
            headers: new Dictionary<string, string>
            {
                [SqsHeaderMapper.CorrelationIdHeader] = "corr-abc",
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/json");

        await adapter.SendBatchAsync([message]);

        captured.Should().NotBeNull();
        // MessageDeduplicationId must be null — broker computes it from content.
        captured!.Entries[0].MessageDeduplicationId.Should().BeNull(
            "content-based dedup means the broker computes the dedup id, not the client");
    }

    [Fact]
    public async Task SendBatchAsync_FifoQueueWithContentBasedDeduplicationDisabled_SetsMessageDeduplicationId()
    {
        const string fifoQueueName = "my-orders.fifo";
        const string fifoQueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/my-orders.fifo";

        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(fifoQueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = fifoQueueUrl }));

        SendMessageBatchRequest? captured = null;
        sqsClient.SendMessageBatchAsync(
                Arg.Any<SendMessageBatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<SendMessageBatchRequest>();
                return Task.FromResult(new SendMessageBatchResponse
                {
                    Successful = captured.Entries.Select(e => new SendMessageBatchResultEntry
                    {
                        Id = e.Id,
                        MessageId = $"msg-{e.Id}",
                    }).ToList(),
                    Failed = [],
                });
            });

        var options = DefaultOptions();
        options.EnableContentBasedDeduplication = false; // explicit for clarity

        var adapter = new SqsTransportAdapter(
            options,
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        var message = new OutboundMessage(
            routingKey: fifoQueueName,
            headers: new Dictionary<string, string>
            {
                [SqsHeaderMapper.CorrelationIdHeader] = "corr-abc",
            },
            body: "hello"u8.ToArray(),
            contentType: "application/json");

        await adapter.SendBatchAsync([message]);

        captured.Should().NotBeNull();
        // MessageDeduplicationId must be non-null and non-empty (generated hash).
        captured!.Entries[0].MessageDeduplicationId.Should().NotBeNullOrEmpty(
            "without content-based dedup, BareWire must generate a deterministic dedup id");
    }
}
