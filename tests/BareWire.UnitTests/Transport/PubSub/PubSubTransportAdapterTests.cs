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

public sealed class PubSubTransportAdapterTests
{
    private static PubSubTransportOptions DefaultOptions() => new()
    {
        AuthMode = PubSubAuthMode.ApplicationDefault,
        ProjectId = "test-project",
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

    // ── TransportName ─────────────────────────────────────────────────────────

    [Fact]
    public void TransportName_IsGooglePubSub()
    {
        var (adapter, _, _) = CreateAdapterWithMocks();

        adapter.TransportName.Should().Be("Google.PubSub");
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_ContainsOrderingKeys()
    {
        var (adapter, _, _) = CreateAdapterWithMocks();

        adapter.Capabilities.HasFlag(TransportCapabilities.OrderingKeys).Should().BeTrue();
    }

    [Fact]
    public void Capabilities_ContainsBatchReceive()
    {
        var (adapter, _, _) = CreateAdapterWithMocks();

        adapter.Capabilities.HasFlag(TransportCapabilities.BatchReceive).Should().BeTrue();
    }

    [Fact]
    public void Capabilities_ContainsDlqNative()
    {
        var (adapter, _, _) = CreateAdapterWithMocks();

        adapter.Capabilities.HasFlag(TransportCapabilities.DlqNative).Should().BeTrue();
    }

    [Fact]
    public void Capabilities_ContainsFlowControl()
    {
        var (adapter, _, _) = CreateAdapterWithMocks();

        adapter.Capabilities.HasFlag(TransportCapabilities.FlowControl).Should().BeTrue();
    }

    [Fact]
    public void Capabilities_ContainsAllFourFlags()
    {
        var (adapter, _, _) = CreateAdapterWithMocks();

        TransportCapabilities expected =
            TransportCapabilities.OrderingKeys |
            TransportCapabilities.BatchReceive |
            TransportCapabilities.DlqNative |
            TransportCapabilities.FlowControl;

        adapter.Capabilities.Should().HaveFlag(expected);
    }

    // ── SendBatchAsync — empty ────────────────────────────────────────────────

    [Fact]
    public async Task SendBatchAsync_EmptyList_ReturnsEmpty()
    {
        var (adapter, _, _) = CreateAdapterWithMocks();

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([]);

        results.Should().BeEmpty();
    }

    // ── SendBatchAsync — basic send, MessageIds → SendResult ──────────────────

    [Fact]
    public async Task SendBatchAsync_ThreeMessages_MapsSendResultsPositionally()
    {
        const string topicName = "my-topic";

        var (adapter, publisher, _) = CreateAdapterWithMocks();

        var response = new PublishResponse();
        response.MessageIds.AddRange(["msg-0", "msg-1", "msg-2"]);

        publisher.PublishAsync(
                Arg.Is<TopicName>(t => t.TopicId == topicName),
                Arg.Any<IEnumerable<PubsubMessage>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        var messages = Enumerable.Range(0, 3)
            .Select(i => new OutboundMessage(
                routingKey: topicName,
                headers: new Dictionary<string, string>(),
                body: ReadOnlyMemory<byte>.Empty,
                contentType: "application/json"))
            .ToList();

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(messages);

        results.Should().HaveCount(3);
        results.All(r => r.IsConfirmed).Should().BeTrue(
            "all three messages got non-empty MessageIds");
        results[0].DeliveryTag.Should().Be(1UL);
        results[1].DeliveryTag.Should().Be(2UL);
        results[2].DeliveryTag.Should().Be(3UL);
    }

    // ── SendBatchAsync — batch count chunking (>1000 messages) ───────────────

    [Fact]
    public async Task SendBatchAsync_MoreThanOneThousandMessages_SplitsIntoMultiplePublishCalls()
    {
        // Arrange — 1500 messages all targeting the same topic → 2 chunks: 1000 + 500.
        const string topicName = "big-topic";

        var (adapter, publisher, _) = CreateAdapterWithMocks();

        publisher.PublishAsync(
                Arg.Is<TopicName>(t => t.TopicId == topicName),
                Arg.Any<IEnumerable<PubsubMessage>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var msgs = callInfo.Arg<IEnumerable<PubsubMessage>>().ToList();
                var resp = new PublishResponse();
                for (int i = 0; i < msgs.Count; i++)
                {
                    resp.MessageIds.Add($"id-{i}");
                }
                return Task.FromResult(resp);
            });

        var messages = Enumerable.Range(0, 1500)
            .Select(_ => new OutboundMessage(
                routingKey: topicName,
                headers: new Dictionary<string, string>(),
                body: ReadOnlyMemory<byte>.Empty,
                contentType: "application/json"))
            .ToList();

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(messages);

        // Assert — exactly 2 publish calls (1000 + 500 = 1500 messages).
        await publisher.Received(2).PublishAsync(
            Arg.Is<TopicName>(t => t.TopicId == topicName),
            Arg.Any<IEnumerable<PubsubMessage>>(),
            Arg.Any<CancellationToken>());

        results.Should().HaveCount(1500);
    }

    // ── SendBatchAsync — PERF-1: attribute-inclusive byte budget splits chunks ─

    [Fact]
    public async Task SendBatchAsync_AttributeBytesPushBudgetOver9_5MB_SplitsChunk()
    {
        // Arrange — 2 messages where the attribute-inclusive byte estimate exceeds 9.5 MB.
        // Strategy: each message has a 4.8 MB body + 100 attributes × 1024-byte values (= 100 KB).
        // Per-message estimate: 4,800,000 + 100 * (key ~5 bytes + 1024 bytes) ≈ 4,902,900 bytes.
        // Two messages combined ≈ 9,805,800 bytes > 9,500,000 (safety margin) → SPLIT into 2 calls.
        // Attribute values stay at exactly 1024 bytes to pass SEC-1 validation.
        const string topicName = "attr-topic";

        var (adapter, publisher, _) = CreateAdapterWithMocks();

        publisher.PublishAsync(
                Arg.Is<TopicName>(t => t.TopicId == topicName),
                Arg.Any<IEnumerable<PubsubMessage>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var msgs = callInfo.Arg<IEnumerable<PubsubMessage>>().ToList();
                var resp = new PublishResponse();
                for (int i = 0; i < msgs.Count; i++)
                {
                    resp.MessageIds.Add($"id-{i}");
                }
                return Task.FromResult(resp);
            });

        byte[] largeBody = new byte[4_800_000]; // 4.8 MB body

        // 100 attributes, each key ~5 chars ("aXXXX") and value = exactly 1024 chars (ASCII = 1024 bytes).
        // SEC-1 limit: key ≤ 256 bytes, value ≤ 1024 bytes — exactly at the limit is valid.
        string attrValue = new string('A', 1024); // exactly 1024 UTF-8 bytes — at the limit (valid)
        var headers = Enumerable.Range(0, 100)
            .ToDictionary(i => $"a{i:D4}", _ => attrValue);  // keys are 5 ASCII chars each

        // Per-message estimate: 4,800,000 (body) + 100 × (5 key + 1024 value) = 4,800,000 + 102,900 = 4,902,900 bytes
        // Two messages: 9,805,800 bytes > 9,500,000 safety margin → must split.
        var message1 = new OutboundMessage(
            routingKey: topicName,
            headers: headers,
            body: largeBody,
            contentType: "application/octet-stream");

        var message2 = new OutboundMessage(
            routingKey: topicName,
            headers: headers,
            body: largeBody,
            contentType: "application/octet-stream");

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([message1, message2]);

        // Assert — must split into 2 separate PublishAsync calls due to attribute-inclusive byte budget.
        await publisher.Received(2).PublishAsync(
            Arg.Is<TopicName>(t => t.TopicId == topicName),
            Arg.Any<IEnumerable<PubsubMessage>>(),
            Arg.Any<CancellationToken>());

        results.Should().HaveCount(2);
        results.All(r => r.IsConfirmed).Should().BeTrue();
    }

    // ── SendBatchAsync — ordering key pass-through (R5.1) ────────────────────

    [Fact]
    public async Task SendBatchAsync_MessageWithOrderingKeyHeader_SetsOrderingKeyOnPubsubMessage()
    {
        const string topicName = "ordered-topic";
        const string orderingKeyValue = "partition-42";

        var (adapter, publisher, _) = CreateAdapterWithMocks();

        IEnumerable<PubsubMessage>? capturedMessages = null;
        publisher.PublishAsync(
                Arg.Any<TopicName>(),
                Arg.Do<IEnumerable<PubsubMessage>>(m => capturedMessages = m.ToList()),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var msgs = callInfo.Arg<IEnumerable<PubsubMessage>>().ToList();
                var resp = new PublishResponse();
                resp.MessageIds.Add("msg-1");
                return Task.FromResult(resp);
            });

        var message = new OutboundMessage(
            routingKey: topicName,
            headers: new Dictionary<string, string> { [PubSubHeaderMapper.OrderingKeyHeader] = orderingKeyValue },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/json");

        await adapter.SendBatchAsync([message]);

        capturedMessages.Should().NotBeNull();
        capturedMessages!.Single().OrderingKey.Should().Be(orderingKeyValue,
            "BW-OrderingKey header must be passed through to PubsubMessage.OrderingKey (R5.1)");
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var (adapter, _, _) = CreateAdapterWithMocks();

        await adapter.DisposeAsync();

        Func<Task> act = async () => await adapter.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}
