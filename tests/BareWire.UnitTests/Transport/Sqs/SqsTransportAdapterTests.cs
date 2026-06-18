using System.Buffers;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using BareWire.Abstractions;
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
}
