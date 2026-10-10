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

/// <summary>
/// AWSSDK.SQS v4 leaves response collections <see langword="null"/> (instead of empty) unless the global
/// <c>AWSConfigs.InitializeCollections</c> switch is set. These tests pin that the adapter treats a null
/// collection as an empty one on every response it consumes.
/// </summary>
public sealed class SqsTransportAdapterNullCollectionsTests
{
    private const string QueueName = "null-collections-queue";
    private const string QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/null-collections-queue";

    private static readonly TimeSpan _guard = TimeSpan.FromSeconds(10);

    private static SqsTransportOptions Options() => new()
    {
        AuthMode = SqsAuthMode.DefaultChain,
        WaitTimeSeconds = 0,
    };

    private static IAmazonSQS CreateClient()
    {
        var client = Substitute.For<IAmazonSQS>();
        client.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));
        return client;
    }

    private static List<OutboundMessage> Outbound(int count) =>
        Enumerable.Range(0, count)
            .Select(_ => new OutboundMessage(QueueName, new Dictionary<string, string>(),
                ReadOnlyMemory<byte>.Empty, "application/json"))
            .ToList();

    [Fact]
    public async Task SendBatchAsync_NullFailedCollection_ReturnsConfirmedResults()
    {
        IAmazonSQS client = CreateClient();
        client.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new SendMessageBatchResponse
            {
                Successful = ci.Arg<SendMessageBatchRequest>().Entries
                    .Select(e => new SendMessageBatchResultEntry { Id = e.Id, MessageId = $"m-{e.Id}" })
                    .ToList(),
                Failed = null,
            }));
        var adapter = new SqsTransportAdapter(Options(), NullLogger<SqsTransportAdapter>.Instance, client);

        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync(Outbound(3), TestContext.Current.CancellationToken);

        results.Should().HaveCount(3);
        results.Should().OnlyContain(r => r.IsConfirmed);
    }

    [Fact]
    public async Task SendBatchAsync_NullSuccessfulCollectionAndPopulatedFailed_ReturnsUnconfirmedResults()
    {
        IAmazonSQS client = CreateClient();
        client.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new SendMessageBatchResponse
            {
                Successful = null,
                Failed = ci.Arg<SendMessageBatchRequest>().Entries
                    .Select(e => new BatchResultErrorEntry { Id = e.Id, Code = "InternalError", Message = "boom" })
                    .ToList(),
            }));
        var adapter = new SqsTransportAdapter(Options(), NullLogger<SqsTransportAdapter>.Instance, client);

        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync(Outbound(2), TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => !r.IsConfirmed);
    }

    [Fact]
    public async Task ConsumeAsync_NullMessagesInReceiveResponse_KeepsPollingAndDeliversLaterMessage()
    {
        IAmazonSQS client = CreateClient();
        int call = 0;
        client.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                int index = Interlocked.Increment(ref call) - 1;
                if (index < 2)
                {
                    return Task.FromResult(new ReceiveMessageResponse { Messages = null });
                }

                if (index == 2)
                {
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = "msg-after-null",
                                ReceiptHandle = "rh",
                                Body = "{}",
                                MessageAttributes = null,
                                Attributes = null,
                            },
                        ],
                    });
                }

                return Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>())
                    .ContinueWith<ReceiveMessageResponse>(_ => throw new OperationCanceledException(),
                        TaskScheduler.Default);
            });
        var adapter = new SqsTransportAdapter(Options(), NullLogger<SqsTransportAdapter>.Instance, client);
        using var cts = new CancellationTokenSource();

        await using IAsyncEnumerator<InboundMessage> enumerator = adapter
            .ConsumeAsync(QueueName, new FlowControlOptions { InternalQueueCapacity = 10 }, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        (await enumerator.MoveNextAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        enumerator.Current.MessageId.Should().Be("msg-after-null");
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ConsumeAsync_NullFailedInVisibilityBatchResponseDuringDrain_CompletesCleanly()
    {
        IAmazonSQS client = CreateClient();
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        client.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages = Enumerable.Range(0, 3)
                            .Select(i => new Message
                            {
                                MessageId = $"m{i}",
                                ReceiptHandle = $"rh{i}",
                                Body = "{}",
                                MessageAttributes = null,
                            })
                            .ToList(),
                    });
                }

                idle.TrySetResult();
                return Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>())
                    .ContinueWith<ReceiveMessageResponse>(_ => throw new OperationCanceledException(),
                        TaskScheduler.Default);
            });
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChangeMessageVisibilityBatchResponse
            {
                Failed = null,
                Successful = null,
            }));
        var adapter = new SqsTransportAdapter(Options(), NullLogger<SqsTransportAdapter>.Instance, client);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = adapter
            .ConsumeAsync(QueueName, new FlowControlOptions { InternalQueueCapacity = 10 }, cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await idle.Task.WaitAsync(_guard, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        await client.Received(1).ChangeMessageVisibilityBatchAsync(
            Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>());
        adapter.InFlightRegistry.Count.Should().Be(1);
    }
}
