using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.Google.PubSub;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubTransportAdapterTopologyTests
{
    private const string ProjectId = "test-project";
    private const string TopicId = "orders-exchange";
    private const string SubscriptionId = "orders-queue";

    private static PubSubTransportOptions DefaultOptions() => new()
    {
        AuthMode = PubSubAuthMode.ApplicationDefault,
        ProjectId = ProjectId,
    };

    private static (PubSubTransportAdapter Adapter, PublisherServiceApiClient Publisher, SubscriberServiceApiClient Subscriber)
        CreateAdapterWithMocks(PubSubTransportOptions? options = null)
    {
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        var adapter = new PubSubTransportAdapter(
            options ?? DefaultOptions(),
            NullLogger<PubSubTransportAdapter>.Instance,
            publisher,
            subscriber);
        return (adapter, publisher, subscriber);
    }

    private static TopologyDeclaration BuildTopology() => new()
    {
        Exchanges = [new ExchangeDeclaration(TopicId, ExchangeType.Topic)],
        Queues = [new QueueDeclaration(SubscriptionId)],
        ExchangeQueueBindings = [new ExchangeQueueBinding(TopicId, SubscriptionId, "#")],
    };

    // ── DeployTopologyAsync — happy path ──────────────────────────────────────

    [Fact]
    public async Task DeployTopologyAsync_OneTopicAndOneSubscription_CallsCreateTopicAndCreateSubscription()
    {
        // Arrange
        var (adapter, publisher, subscriber) = CreateAdapterWithMocks();

        publisher.CreateTopicAsync(
                Arg.Any<TopicName>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new Topic
            {
                TopicName = callInfo.Arg<TopicName>(),
            }));

        subscriber.CreateSubscriptionAsync(
                Arg.Any<Subscription>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<Subscription>()));

        // Act
        await adapter.DeployTopologyAsync(BuildTopology());

        // Assert — topic created with the expected topic name.
        await publisher.Received(1).CreateTopicAsync(
            Arg.Is<TopicName>(t => t.ProjectId == ProjectId && t.TopicId == TopicId),
            Arg.Any<CancellationToken>());

        // Assert — subscription created with the expected subscription name.
        await subscriber.Received(1).CreateSubscriptionAsync(
            Arg.Is<Subscription>(s =>
                s.SubscriptionName.ProjectId == ProjectId &&
                s.SubscriptionName.SubscriptionId == SubscriptionId),
            Arg.Any<CancellationToken>());
    }

    // ── DeployTopologyAsync — idempotence: AlreadyExists is swallowed ─────────

    [Fact]
    public async Task DeployTopologyAsync_CreateTopicThrowsAlreadyExists_DoesNotThrow()
    {
        // Arrange — CreateTopicAsync raises AlreadyExists (idempotent re-declaration).
        var (adapter, publisher, subscriber) = CreateAdapterWithMocks();

        publisher.CreateTopicAsync(
                Arg.Any<TopicName>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<Topic>>(_ =>
                throw new RpcException(new Status(StatusCode.AlreadyExists, "Topic already exists")));

        subscriber.CreateSubscriptionAsync(
                Arg.Any<Subscription>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<Subscription>()));

        // Act & Assert — must not throw; AlreadyExists is swallowed as an idempotent signal.
        Func<Task> act = async () => await adapter.DeployTopologyAsync(BuildTopology());
        await act.Should().NotThrowAsync(
            "AlreadyExists on CreateTopicAsync is the idempotent signal — DeployTopologyAsync must swallow it");
    }

    [Fact]
    public async Task DeployTopologyAsync_CreateSubscriptionThrowsAlreadyExists_DoesNotThrow()
    {
        // Arrange — CreateSubscriptionAsync raises AlreadyExists (idempotent re-declaration).
        var (adapter, publisher, subscriber) = CreateAdapterWithMocks();

        publisher.CreateTopicAsync(
                Arg.Any<TopicName>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new Topic
            {
                TopicName = callInfo.Arg<TopicName>(),
            }));

        subscriber.CreateSubscriptionAsync(
                Arg.Any<Subscription>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<Subscription>>(_ =>
                throw new RpcException(new Status(StatusCode.AlreadyExists, "Subscription already exists")));

        // Act & Assert — must not throw; AlreadyExists on subscription is also idempotent.
        Func<Task> act = async () => await adapter.DeployTopologyAsync(BuildTopology());
        await act.Should().NotThrowAsync(
            "AlreadyExists on CreateSubscriptionAsync is the idempotent signal — DeployTopologyAsync must swallow it");
    }
}
