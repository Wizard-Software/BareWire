using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Google.PubSub;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubTransportAdapterFullChannelTests
{
    private const string ProjectId = "test-project";
    private const string SubscriptionId = "full-channel-sub";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private static PubSubTransportOptions Options() => new()
    {
        AuthMode = PubSubAuthMode.ApplicationDefault,
        ProjectId = ProjectId,
    };

    // Completes only when the token is cancelled, so a pull loop never spins on empty responses.
    private static async Task<PullResponse> AwaitCancellationAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<PullResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration registration =
            cancellationToken.Register(static state =>
                ((TaskCompletionSource<PullResponse>)state!).TrySetCanceled(), tcs);
        return await tcs.Task;
    }

    private static PullResponse SingleMessage(int n)
    {
        var response = new PullResponse();
        response.ReceivedMessages.Add(new ReceivedMessage
        {
            AckId = $"ack-{n}",
            Message = new PubsubMessage { MessageId = $"msg-{n}", Data = ByteString.CopyFromUtf8("{}") },
        });
        return response;
    }

    // The channel holds one message while the endpoint buffer allows ten: a pull that finds the channel full
    // must not lose the message it received.
    [Fact]
    public async Task ConsumeAsync_InternalQueueSmallerThanPrefetch_DeliversEveryPulledMessage()
    {
        // Arrange
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();

        var fourthCallSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        subscriber.PullAsync(Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                int call = Interlocked.Increment(ref callCount);
                if (call <= 3)
                {
                    return Task.FromResult(SingleMessage(call));
                }

                fourthCallSignal.TrySetResult();
                return AwaitCancellationAsync(callInfo.ArgAt<CancellationToken>(2));
            });

        var adapter = new PubSubTransportAdapter(
            Options(), NullLogger<PubSubTransportAdapter>.Instance, publisher, subscriber);
        var flowControl = new FlowControlOptions
        {
            InternalQueueCapacity = 1,
            MaxInFlightMessages = 10,
            FullMode = BoundedChannelFullMode.Wait,
        };

        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, flowControl, cts.Token).GetAsyncEnumerator(cts.Token);

        try
        {
            // Act — the first MoveNextAsync starts the pull loop, so it must run before waiting on the signal.
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
            tags.Distinct().Should().HaveCount(3, "every pulled message must reach the consumer");
            await subscriber.DidNotReceive().ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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
    // Regression guard: the negative InternalQueueCapacity case already throws today (from the channel
    // options), so it is not RED before the change; it pins that the broker is never pulled from.
    [InlineData(BoundedChannelFullMode.Wait, 100, 0)]
    [InlineData(BoundedChannelFullMode.Wait, 100, -1)]
    public async Task ConsumeAsync_InvalidFlowControl_ThrowsBeforeAnyBrokerCall(
        BoundedChannelFullMode fullMode, int maxInFlightMessages, int internalQueueCapacity)
    {
        // Arrange
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        subscriber.PullAsync(Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => AwaitCancellationAsync(callInfo.ArgAt<CancellationToken>(2)));

        var adapter = new PubSubTransportAdapter(
            Options(), NullLogger<PubSubTransportAdapter>.Instance, publisher, subscriber);
        var flowControl = new FlowControlOptions
        {
            MaxInFlightMessages = maxInFlightMessages,
            InternalQueueCapacity = internalQueueCapacity,
            FullMode = fullMode,
        };

        using var cts = new CancellationTokenSource(s_timeout);
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, flowControl, cts.Token).GetAsyncEnumerator(cts.Token);

        try
        {
            // Act
            Func<Task> act = async () => await enumerator.MoveNextAsync();

            // Assert
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            await subscriber.DidNotReceive().PullAsync(
                Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
            await subscriber.DidNotReceive().ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
            await subscriber.DidNotReceive().AcknowledgeAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await cts.CancelAsync();
            await enumerator.DisposeAsync();
        }
    }
}
