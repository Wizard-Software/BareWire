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

public sealed class SqsTransportAdapterConsumerTests
{
    private const string QueueName = "test-queue";
    private const string QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/test-queue";
    private const string ReceiptHandle = "receipt-handle-xyz";

    private static SqsTransportOptions DefaultOptions() => new()
    {
        AuthMode = SqsAuthMode.DefaultChain,
        WaitTimeSeconds = 0, // 0 for tests — no actual blocking
    };

    // ── ConsumeAsync — one message ────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_OneMessage_YieldsInboundMessageWithCorrectDeliveryTag()
    {
        // Arrange
        var sqsClient = Substitute.For<IAmazonSQS>();

        sqsClient.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));

        int callCount = 0;
        sqsClient.ReceiveMessageAsync(
                Arg.Any<ReceiveMessageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // First call: return one message. Subsequent: cancel to break the loop.
                if (callCount++ == 0)
                {
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = "msg-001",
                                ReceiptHandle = ReceiptHandle,
                                Body = "{\"hello\":\"world\"}",
                                MessageAttributes = [],
                            },
                        ],
                    });
                }

                // Throw to break the polling loop (simulates cancellation from upstream).
                throw new OperationCanceledException();
            });

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        using var cts = new CancellationTokenSource();
        var flowControl = new FlowControlOptions { InternalQueueCapacity = 10 };

        InboundMessage? received = null;

        // Act — enumerate only the first message.
        await foreach (InboundMessage msg in adapter.ConsumeAsync(QueueName, flowControl, cts.Token))
        {
            received = msg;
            cts.Cancel(); // stop after first message
            break;
        }

        // Assert
        received.Should().NotBeNull();
        received!.MessageId.Should().Be("msg-001");
        received.DeliveryTag.Should().BeGreaterThan(0UL, "DeliveryTag is a monotonic counter");
    }

    // ── SettleAsync(Ack) ──────────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Ack_CallsDeleteMessageWithCorrectReceiptHandle()
    {
        // Arrange
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));

        int callCount = 0;
        sqsClient.ReceiveMessageAsync(
                Arg.Any<ReceiveMessageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (callCount++ == 0)
                {
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = "settle-msg-001",
                                ReceiptHandle = ReceiptHandle,
                                Body = "{}",
                                MessageAttributes = [],
                            },
                        ],
                    });
                }

                throw new OperationCanceledException();
            });

        sqsClient.DeleteMessageAsync(
                Arg.Any<DeleteMessageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeleteMessageResponse()));

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        using var cts = new CancellationTokenSource();
        var flowControl = new FlowControlOptions { InternalQueueCapacity = 10 };

        InboundMessage? received = null;

        await foreach (InboundMessage msg in adapter.ConsumeAsync(QueueName, flowControl, cts.Token))
        {
            received = msg;
            cts.Cancel();
            break;
        }

        received.Should().NotBeNull();

        // Act
        await adapter.SettleAsync(SettlementAction.Ack, received!);

        // Assert — DeleteMessage must be called with the correct ReceiptHandle.
        await sqsClient.Received(1).DeleteMessageAsync(
            Arg.Is<DeleteMessageRequest>(r =>
                r.QueueUrl == QueueUrl && r.ReceiptHandle == ReceiptHandle),
            Arg.Any<CancellationToken>());
    }

    // ── SettleAsync(Reject) — does NOT call DeleteMessageAsync ────────────────

    [Fact]
    public async Task SettleAsync_Reject_DoesNotCallDeleteMessageAsync()
    {
        // ADR-014 / GAP-3: Reject must NOT call DeleteMessage on the source queue.
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));

        int callCount = 0;
        sqsClient.ReceiveMessageAsync(
                Arg.Any<ReceiveMessageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (callCount++ == 0)
                {
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = "reject-msg",
                                ReceiptHandle = ReceiptHandle,
                                Body = "{}",
                                MessageAttributes = [],
                            },
                        ],
                    });
                }

                throw new OperationCanceledException();
            });

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        using var cts = new CancellationTokenSource();
        var flowControl = new FlowControlOptions { InternalQueueCapacity = 10 };

        InboundMessage? received = null;

        await foreach (InboundMessage msg in adapter.ConsumeAsync(QueueName, flowControl, cts.Token))
        {
            received = msg;
            cts.Cancel();
            break;
        }

        received.Should().NotBeNull();

        // Act
        await adapter.SettleAsync(SettlementAction.Reject, received!);

        // Assert — DeleteMessage must NOT be called (ADR-014 / GAP-3).
        await sqsClient.DidNotReceive().DeleteMessageAsync(
            Arg.Any<DeleteMessageRequest>(),
            Arg.Any<CancellationToken>());

        // Also must not change visibility (Reject = do nothing destructive).
        await sqsClient.DidNotReceive().ChangeMessageVisibilityAsync(
            Arg.Any<ChangeMessageVisibilityRequest>(),
            Arg.Any<CancellationToken>());
    }

    // ── SettleAsync — evict-once ──────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_SecondSettleOnSameMessage_ThrowsBareWireTransportException()
    {
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));

        int callCount = 0;
        sqsClient.ReceiveMessageAsync(
                Arg.Any<ReceiveMessageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (callCount++ == 0)
                {
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = "dup-msg",
                                ReceiptHandle = ReceiptHandle,
                                Body = "{}",
                                MessageAttributes = [],
                            },
                        ],
                    });
                }

                throw new OperationCanceledException();
            });

        sqsClient.DeleteMessageAsync(
                Arg.Any<DeleteMessageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeleteMessageResponse()));

        var adapter = new SqsTransportAdapter(
            DefaultOptions(),
            NullLogger<SqsTransportAdapter>.Instance,
            sqsClient);

        using var cts = new CancellationTokenSource();
        var flowControl = new FlowControlOptions { InternalQueueCapacity = 10 };

        InboundMessage? received = null;
        await foreach (InboundMessage msg in adapter.ConsumeAsync(QueueName, flowControl, cts.Token))
        {
            received = msg;
            cts.Cancel();
            break;
        }

        // First settle succeeds.
        await adapter.SettleAsync(SettlementAction.Ack, received!);

        // Second settle must throw (evict-once).
        Func<Task> act = async () => await adapter.SettleAsync(SettlementAction.Ack, received!);
        await act.Should().ThrowAsync<BareWire.Abstractions.Exceptions.BareWireTransportException>();
    }
}
