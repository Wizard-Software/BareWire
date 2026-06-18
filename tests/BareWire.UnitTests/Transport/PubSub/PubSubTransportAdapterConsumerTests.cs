using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Google.PubSub;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubTransportAdapterConsumerTests
{
    private const string SubscriptionId = "test-subscription";
    private const string AckId = "ack-id-xyz";

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

    private static byte[] FlattenSequence(System.Buffers.ReadOnlySequence<byte> sequence)
    {
        byte[] buffer = new byte[sequence.Length];
        Span<byte> destination = buffer;
        foreach (ReadOnlyMemory<byte> segment in sequence)
        {
            segment.Span.CopyTo(destination);
            destination = destination[segment.Length..];
        }
        return buffer;
    }

    private static PullResponse BuildPullResponse(
        string ackId,
        byte[] body,
        string? attributeKey = null,
        string? attributeValue = null,
        string? orderingKey = null)
    {
        var pubsubMessage = new PubsubMessage
        {
            MessageId = "msg-001",
            Data = ByteString.CopyFrom(body),
            OrderingKey = orderingKey ?? string.Empty,
        };

        if (attributeKey is not null && attributeValue is not null)
        {
            pubsubMessage.Attributes.Add(attributeKey, attributeValue);
        }

        var receivedMessage = new ReceivedMessage
        {
            AckId = ackId,
            Message = pubsubMessage,
        };

        var response = new PullResponse();
        response.ReceivedMessages.Add(receivedMessage);
        return response;
    }

    // ── ConsumeAsync — one message ────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_OneMessage_YieldsInboundMessageWithNonZeroDeliveryTagAndMappedHeader()
    {
        // Arrange
        byte[] expectedBody = [0x01, 0x02, 0x03];
        const string headerKey = "BW-CorrelationId";
        const string headerValue = "corr-42";

        var (adapter, _, subscriber) = CreateAdapterWithMocks();

        int callCount = 0;
        subscriber.PullAsync(
                Arg.Any<SubscriptionName>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (callCount++ == 0)
                {
                    return Task.FromResult(BuildPullResponse(
                        ackId: AckId,
                        body: expectedBody,
                        attributeKey: headerKey,
                        attributeValue: headerValue));
                }

                throw new OperationCanceledException();
            });

        using var cts = new CancellationTokenSource();
        var flowControl = new FlowControlOptions { InternalQueueCapacity = 10 };

        InboundMessage? received = null;

        // Act — enumerate only the first message, then cancel to terminate the polling loop.
        await foreach (InboundMessage msg in adapter.ConsumeAsync(SubscriptionId, flowControl, cts.Token))
        {
            received = msg;
            cts.Cancel();
            break;
        }

        // Assert
        received.Should().NotBeNull();
        received!.DeliveryTag.Should().BeGreaterThan(0UL, "DeliveryTag is a monotonic counter starting at 1");
        received.Headers.Should().ContainKey(headerKey)
            .WhoseValue.Should().Be(headerValue, "Pub/Sub Attribute must be mapped to BareWire header");

        // Body round-trip: bytes read back from the ReadOnlySequence must match the original body.
        // Flatten via SequenceReader to avoid ambiguity with ImmutableArrayExtensions.ToArray.
        byte[] actualBody = received.Body.IsSingleSegment
            ? received.Body.FirstSpan.ToArray()
            : FlattenSequence(received.Body);
        actualBody.Should().Equal(expectedBody, "body bytes must survive the Pub/Sub → BareWire mapping");
    }

    // ── SettleAsync(Ack) ──────────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Ack_CallsAcknowledgeAsyncWithCorrectAckId()
    {
        // Arrange
        var (adapter, _, subscriber) = CreateAdapterWithMocks();

        int callCount = 0;
        subscriber.PullAsync(
                Arg.Any<SubscriptionName>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (callCount++ == 0)
                {
                    return Task.FromResult(BuildPullResponse(ackId: AckId, body: []));
                }

                throw new OperationCanceledException();
            });

        subscriber.AcknowledgeAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var flowControl = new FlowControlOptions { InternalQueueCapacity = 10 };

        InboundMessage? received = null;

        await foreach (InboundMessage msg in adapter.ConsumeAsync(SubscriptionId, flowControl, cts.Token))
        {
            received = msg;
            cts.Cancel();
            break;
        }

        received.Should().NotBeNull();

        // Act
        await adapter.SettleAsync(SettlementAction.Ack, received!);

        // Assert — AcknowledgeAsync must be called exactly once with the registered ackId.
        await subscriber.Received(1).AcknowledgeAsync(
            Arg.Any<string>(),
            Arg.Is<IEnumerable<string>>(ids => ids.Contains(AckId)),
            Arg.Any<CancellationToken>());
    }

    // ── SettleAsync(Nack) ─────────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Nack_CallsModifyAckDeadlineAsyncWithDeadlineZero()
    {
        // Arrange
        var (adapter, _, subscriber) = CreateAdapterWithMocks();

        int callCount = 0;
        subscriber.PullAsync(
                Arg.Any<SubscriptionName>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (callCount++ == 0)
                {
                    return Task.FromResult(BuildPullResponse(ackId: AckId, body: []));
                }

                throw new OperationCanceledException();
            });

        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var flowControl = new FlowControlOptions { InternalQueueCapacity = 10 };

        InboundMessage? received = null;

        await foreach (InboundMessage msg in adapter.ConsumeAsync(SubscriptionId, flowControl, cts.Token))
        {
            received = msg;
            cts.Cancel();
            break;
        }

        received.Should().NotBeNull();

        // Act
        await adapter.SettleAsync(SettlementAction.Nack, received!);

        // Assert — ModifyAckDeadlineAsync must be called with ackDeadlineSeconds = 0 (Pub/Sub nack idiom).
        await subscriber.Received(1).ModifyAckDeadlineAsync(
            Arg.Any<string>(),
            Arg.Is<IEnumerable<string>>(ids => ids.Contains(AckId)),
            Arg.Is<int>(d => d == 0),
            Arg.Any<CancellationToken>());
    }
}
