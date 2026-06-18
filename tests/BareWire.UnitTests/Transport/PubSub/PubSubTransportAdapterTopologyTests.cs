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

    // ── DeployTopologyAsync — DLQ wiring ─────────────────────────────────────

    private static TopologyDeclaration BuildTopologyWithDlq(
        string deadLetterTopic = "orders-dlq",
        int maxDeliveryAttempts = 7) => new()
    {
        Exchanges = [new ExchangeDeclaration(TopicId, ExchangeType.Topic)],
        Queues =
        [
            new QueueDeclaration(
                Name: SubscriptionId,
                Arguments: new Dictionary<string, object>
                {
                    ["bw.pubsub.dead-letter-topic"] = deadLetterTopic,
                    ["bw.pubsub.max-delivery-attempts"] = maxDeliveryAttempts,
                }),
        ],
        ExchangeQueueBindings = [new ExchangeQueueBinding(TopicId, SubscriptionId, "#")],
    };

    [Fact]
    public async Task DeployTopologyAsync_QueueWithDeadLetterTopicArg_AppliesDeadLetterPolicyToSubscription()
    {
        // Arrange
        var (adapter, publisher, subscriber) = CreateAdapterWithMocks();

        publisher.CreateTopicAsync(Arg.Any<TopicName>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new Topic { TopicName = callInfo.Arg<TopicName>() }));

        Subscription? captured = null;
        subscriber.CreateSubscriptionAsync(Arg.Any<Subscription>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<Subscription>();
                return Task.FromResult(captured);
            });

        // Act
        await adapter.DeployTopologyAsync(BuildTopologyWithDlq("orders-dlq", maxDeliveryAttempts: 7));

        // Assert — subscription received a DeadLetterPolicy with the full resource name and correct attempt count.
        captured.Should().NotBeNull();
        captured!.DeadLetterPolicy.Should().NotBeNull(
            "a queue with bw.pubsub.dead-letter-topic must have DeadLetterPolicy applied");
        captured.DeadLetterPolicy.DeadLetterTopic.Should().Be(
            $"projects/{ProjectId}/topics/orders-dlq",
            "DeadLetterPolicy.DeadLetterTopic must be the full resource name");
        captured.DeadLetterPolicy.MaxDeliveryAttempts.Should().Be(7,
            "MaxDeliveryAttempts must equal the bw.pubsub.max-delivery-attempts argument");
    }

    [Fact]
    public async Task DeployTopologyAsync_QueueWithDeadLetterTopicArg_CreatesDeadLetterTopic()
    {
        // Arrange
        var (adapter, publisher, subscriber) = CreateAdapterWithMocks();

        publisher.CreateTopicAsync(Arg.Any<TopicName>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new Topic { TopicName = callInfo.Arg<TopicName>() }));

        subscriber.CreateSubscriptionAsync(Arg.Any<Subscription>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<Subscription>()));

        // Act
        await adapter.DeployTopologyAsync(BuildTopologyWithDlq("orders-dlq"));

        // Assert — CreateTopicAsync must have been called for the DLQ topic.
        await publisher.Received().CreateTopicAsync(
            Arg.Is<TopicName>(t => t.TopicId == "orders-dlq"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeployTopologyAsync_QueueWithoutDeadLetterTopicArg_DoesNotSetDeadLetterPolicy()
    {
        // Arrange
        var (adapter, publisher, subscriber) = CreateAdapterWithMocks();

        publisher.CreateTopicAsync(Arg.Any<TopicName>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new Topic { TopicName = callInfo.Arg<TopicName>() }));

        Subscription? captured = null;
        subscriber.CreateSubscriptionAsync(Arg.Any<Subscription>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<Subscription>();
                return Task.FromResult(captured);
            });

        // Act — use the standard topology with no DLQ arguments.
        await adapter.DeployTopologyAsync(BuildTopology());

        // Assert — no DeadLetterPolicy on the subscription.
        captured.Should().NotBeNull();
        captured!.DeadLetterPolicy.Should().BeNull(
            "a queue without bw.pubsub.dead-letter-topic must not have DeadLetterPolicy set");

        // Assert — CreateTopicAsync called only for the source topic (TopicId), not for any DLQ topic.
        await publisher.Received(1).CreateTopicAsync(
            Arg.Is<TopicName>(t => t.TopicId == TopicId),
            Arg.Any<CancellationToken>());
        await publisher.DidNotReceive().CreateTopicAsync(
            Arg.Is<TopicName>(t => t.TopicId != TopicId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeployTopologyAsync_DeadLetterTopicAlreadyExists_DoesNotThrow()
    {
        // Arrange — CreateTopicAsync returns AlreadyExists for the DLQ topic; source topic succeeds.
        var (adapter, publisher, subscriber) = CreateAdapterWithMocks();

        publisher.CreateTopicAsync(
                Arg.Is<TopicName>(t => t.TopicId == TopicId),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new Topic { TopicName = callInfo.Arg<TopicName>() }));

        publisher.CreateTopicAsync(
                Arg.Is<TopicName>(t => t.TopicId == "orders-dlq"),
                Arg.Any<CancellationToken>())
            .Returns<Task<Topic>>(_ =>
                throw new RpcException(new Status(StatusCode.AlreadyExists, "Topic already exists")));

        Subscription? captured = null;
        subscriber.CreateSubscriptionAsync(Arg.Any<Subscription>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<Subscription>();
                return Task.FromResult(captured);
            });

        // Act & Assert — AlreadyExists on DLQ topic is idempotent; no exception and subscription still created with policy.
        Func<Task> act = async () => await adapter.DeployTopologyAsync(BuildTopologyWithDlq("orders-dlq"));
        await act.Should().NotThrowAsync(
            "AlreadyExists on the DLQ CreateTopicAsync is idempotent and must be swallowed");

        captured.Should().NotBeNull();
        captured!.DeadLetterPolicy.Should().NotBeNull(
            "DeadLetterPolicy must still be applied even when the DLQ topic AlreadyExists");
        captured.DeadLetterPolicy.DeadLetterTopic.Should().Be($"projects/{ProjectId}/topics/orders-dlq");
    }

    [Fact]
    public async Task DeployTopologyAsync_DeadLetterTopicCreationFails_ThrowsTopologyDeploymentException()
    {
        // Arrange — source topic creation succeeds; DLQ topic creation fails with a hard error.
        var (adapter, publisher, subscriber) = CreateAdapterWithMocks();

        publisher.CreateTopicAsync(
                Arg.Is<TopicName>(t => t.TopicId == TopicId),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new Topic { TopicName = callInfo.Arg<TopicName>() }));

        publisher.CreateTopicAsync(
                Arg.Is<TopicName>(t => t.TopicId == "orders-dlq"),
                Arg.Any<CancellationToken>())
            .Returns<Task<Topic>>(_ =>
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Permission denied")));

        subscriber.CreateSubscriptionAsync(Arg.Any<Subscription>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<Subscription>()));

        // Act & Assert — a hard RPC failure on DLQ topic creation must surface as TopologyDeploymentException.
        Func<Task> act = async () => await adapter.DeployTopologyAsync(BuildTopologyWithDlq("orders-dlq"));
        var ex = await act.Should().ThrowAsync<BareWire.Abstractions.Exceptions.TopologyDeploymentException>(
            "a non-AlreadyExists RPC failure on the DLQ CreateTopicAsync must be wrapped in TopologyDeploymentException");

        ex.Which.TopologyElement.Should().Be("orders-dlq",
            "TopologyElement must equal the DLQ topic name");
    }
}
