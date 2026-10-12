using System.Threading.Channels;
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

public sealed class SqsTransportAdapterFullChannelTests
{
    private const string QueueName = "test-queue";
    private const string QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/test-queue";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private static SqsTransportOptions DefaultOptions() => new()
    {
        AuthMode = SqsAuthMode.DefaultChain,
        WaitTimeSeconds = 0,
    };

    // Completes only when the token is cancelled, so a polling loop never spins on empty responses.
    private static async Task<ReceiveMessageResponse> AwaitCancellationAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ReceiveMessageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration registration =
            cancellationToken.Register(static state =>
                ((TaskCompletionSource<ReceiveMessageResponse>)state!).TrySetCanceled(), tcs);
        return await tcs.Task;
    }

    [Fact]
    public async Task ConsumeAsync_InternalQueueSmallerThanPrefetch_DeliversEveryPolledMessage()
    {
        // Arrange
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));

        var fourthCallSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        sqsClient.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                int call = Interlocked.Increment(ref callCount);
                if (call <= 3)
                {
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = $"msg-{call}",
                                ReceiptHandle = $"handle-{call}",
                                Body = "{}",
                                MessageAttributes = [],
                            },
                        ],
                    });
                }

                fourthCallSignal.TrySetResult();
                return AwaitCancellationAsync(callInfo.ArgAt<CancellationToken>(1));
            });

        var adapter = new SqsTransportAdapter(
            DefaultOptions(), NullLogger<SqsTransportAdapter>.Instance, sqsClient);
        var flowControl = new FlowControlOptions
        {
            InternalQueueCapacity = 1,
            MaxInFlightMessages = 10,
            FullMode = BoundedChannelFullMode.Wait,
        };

        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(QueueName, flowControl, cts.Token).GetAsyncEnumerator(cts.Token);

        try
        {
            // Act — the first MoveNextAsync starts the polling loop, so it must run before waiting on the signal.
            ValueTask<bool> first = enumerator.MoveNextAsync();
            await fourthCallSignal.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

            var tags = new List<ulong>();
            (await first.AsTask().WaitAsync(s_timeout, TestContext.Current.CancellationToken)).Should().BeTrue();
            tags.Add(enumerator.Current.DeliveryTag);

            for (int i = 0; i < 2; i++)
            {
                bool moved = await enumerator.MoveNextAsync().AsTask()
                    .WaitAsync(s_timeout, TestContext.Current.CancellationToken);
                moved.Should().BeTrue();
                tags.Add(enumerator.Current.DeliveryTag);
            }

            // Assert — checked before the consumer stops, so shutdown handback calls cannot interfere.
            tags.Distinct().Should().HaveCount(3, "every polled message must reach the consumer");
            await sqsClient.DidNotReceive().ChangeMessageVisibilityAsync(
                Arg.Any<ChangeMessageVisibilityRequest>(), Arg.Any<CancellationToken>());
            await sqsClient.DidNotReceive().ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await cts.CancelAsync();
            await enumerator.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropWrite, 100, 1000)]
    [InlineData(BoundedChannelFullMode.DropOldest, 100, 1000)]
    [InlineData(BoundedChannelFullMode.DropNewest, 100, 1000)]
    [InlineData(BoundedChannelFullMode.Wait, 0, 1000)]
    [InlineData(BoundedChannelFullMode.Wait, -1, 1000)]
    // A zero InternalQueueCapacity used to be accepted by the channel options, and a negative one threw
    // only after the queue URL lookup; both must now be rejected before the broker is contacted.
    [InlineData(BoundedChannelFullMode.Wait, 100, 0)]
    [InlineData(BoundedChannelFullMode.Wait, 100, -1)]
    public async Task ConsumeAsync_InvalidFlowControl_ThrowsBeforeAnyBrokerCall(
        BoundedChannelFullMode fullMode, int maxInFlightMessages, int internalQueueCapacity)
    {
        // Arrange
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));
        sqsClient.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => AwaitCancellationAsync(callInfo.ArgAt<CancellationToken>(1)));

        var adapter = new SqsTransportAdapter(
            DefaultOptions(), NullLogger<SqsTransportAdapter>.Instance, sqsClient);
        var flowControl = new FlowControlOptions
        {
            MaxInFlightMessages = maxInFlightMessages,
            InternalQueueCapacity = internalQueueCapacity,
            FullMode = fullMode,
        };

        using var cts = new CancellationTokenSource(s_timeout);
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(QueueName, flowControl, cts.Token).GetAsyncEnumerator(cts.Token);

        try
        {
            // Act
            Func<Task> act = async () => await enumerator.MoveNextAsync();

            // Assert
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            await sqsClient.DidNotReceive().GetQueueUrlAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>());
            await sqsClient.DidNotReceive().ReceiveMessageAsync(
                Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await cts.CancelAsync();
            await enumerator.DisposeAsync();
        }
    }
}
