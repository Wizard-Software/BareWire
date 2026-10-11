using System.Buffers;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Internal;
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

    // ── ConsumeAsync — FIFO system attributes stamped on inbound ─────────────

    [Fact]
    public async Task ConsumeAsync_MessageWithFifoSystemAttributes_StampsMessageGroupIdAndSequenceNumber()
    {
        // Arrange — broker sets MessageGroupId and SequenceNumber as system attributes.
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
                                MessageId = "fifo-msg-001",
                                ReceiptHandle = ReceiptHandle,
                                Body = "{\"hello\":\"world\"}",
                                MessageAttributes = [],
                                // System attributes set by the FIFO broker.
                                Attributes = new Dictionary<string, string>
                                {
                                    ["MessageGroupId"] = "grp-1",
                                    ["SequenceNumber"] = "111",
                                },
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

        // Act
        await foreach (InboundMessage msg in adapter.ConsumeAsync(QueueName, flowControl, cts.Token))
        {
            received = msg;
            cts.Cancel();
            break;
        }

        // Assert — BW-MessageGroupId and BW-SequenceNumber must be stamped from system attributes.
        received.Should().NotBeNull();
        received!.Headers.Should().ContainKey(SqsHeaderMapper.MessageGroupIdHeader)
            .WhoseValue.Should().Be("grp-1");
        received.Headers.Should().ContainKey(SqsHeaderMapper.SequenceNumberHeader)
            .WhoseValue.Should().Be("111");
    }

    [Fact]
    public async Task ConsumeAsync_MessageAttributeNamedBwMessageGroupId_DoesNotOverrideTrustedSystemAttribute()
    {
        // SEC-3 anti-squatting: sender sets MessageAttribute "BW-MessageGroupId" = "spoofed".
        // The broker's system attribute "MessageGroupId" = "real" must win because stamping
        // happens AFTER MapInbound (which processes MessageAttributes), overwriting the spoofed value.
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
                                MessageId = "spoof-test-msg",
                                ReceiptHandle = ReceiptHandle,
                                Body = "{}",
                                // Sender-controlled MessageAttributes — untrusted.
                                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                                {
                                    [SqsHeaderMapper.MessageGroupIdHeader] = new MessageAttributeValue
                                    {
                                        DataType = "String",
                                        StringValue = "spoofed",
                                    },
                                },
                                // Broker-controlled system attribute — trusted.
                                Attributes = new Dictionary<string, string>
                                {
                                    ["MessageGroupId"] = "real",
                                },
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

        // Assert — trusted system attribute ("real") must win over spoofed MessageAttribute ("spoofed").
        received.Should().NotBeNull();
        received!.Headers.Should().ContainKey(SqsHeaderMapper.MessageGroupIdHeader)
            .WhoseValue.Should().Be("real",
                "system attributes stamped after MapInbound must override sender-supplied MessageAttributes");
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

    // ── Shared in-flight registry across concurrent endpoints ────────────────

    [Fact]
    public async Task ConsumeAsync_TwoConcurrentEndpoints_ShareSingleInFlightRegistryAndBothSettle()
    {
        // The registry is created eagerly in the constructor, so concurrent ConsumeAsync calls (one per
        // receive endpoint) cannot race on a lazy initialization and register into an abandoned instance.
        var sqsClient = Substitute.For<IAmazonSQS>();
        sqsClient.GetQueueUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new GetQueueUrlResponse
            {
                QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/" + callInfo.ArgAt<string>(0),
            }));

        var served = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        sqsClient.ReceiveMessageAsync(
                Arg.Any<ReceiveMessageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                string queueUrl = callInfo.Arg<ReceiveMessageRequest>().QueueUrl;
                if (served.AddOrUpdate(queueUrl, 1, static (_, n) => n + 1) == 1)
                {
                    return Task.FromResult(new ReceiveMessageResponse
                    {
                        Messages =
                        [
                            new Message
                            {
                                MessageId = "msg-" + queueUrl,
                                ReceiptHandle = "rh-" + queueUrl,
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

        SqsInFlightRegistry registryBefore = adapter.InFlightRegistry;

        async Task<InboundMessage> ConsumeFirstAsync(string queue)
        {
            using var cts = new CancellationTokenSource();
            await foreach (InboundMessage msg in adapter.ConsumeAsync(
                queue, new FlowControlOptions { InternalQueueCapacity = 10 }, cts.Token))
            {
                cts.Cancel();
                return msg;
            }

            throw new InvalidOperationException("No message was received.");
        }

        InboundMessage[] received = await Task.WhenAll(ConsumeFirstAsync("queue-a"), ConsumeFirstAsync("queue-b"));

        adapter.InFlightRegistry.Should().BeSameAs(registryBefore);
        foreach (InboundMessage message in received)
        {
            await adapter.SettleAsync(SettlementAction.Ack, message, TestContext.Current.CancellationToken);
        }

        await sqsClient.Received(1).DeleteMessageAsync(
            Arg.Is<DeleteMessageRequest>(r => r.ReceiptHandle.EndsWith("queue-a", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await sqsClient.Received(1).DeleteMessageAsync(
            Arg.Is<DeleteMessageRequest>(r => r.ReceiptHandle.EndsWith("queue-b", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // ── Slot reservation, endpoint buffer cap and batch return ───────────────
    //
    // The harness yields on every receive (an async substitute), because the polling loop starts
    // synchronously on the first MoveNextAsync: a substitute returning an already-completed task would spin
    // a non-awaiting loop forever instead of failing the test. Every test is bounded by a 10 s token.

    private static readonly TimeSpan _guard = TimeSpan.FromSeconds(10);

    private static int _messageSeed;

    private static Message NewMessage(string? receiptHandle = null)
    {
        int n = Interlocked.Increment(ref _messageSeed);
        return new Message
        {
            MessageId = $"msg-{n}",
            ReceiptHandle = receiptHandle ?? $"rh-{n}",
            Body = "{}",
            MessageAttributes = [],
        };
    }

    private static ReceiveMessageResponse NewBatch(int count)
    {
        var messages = new List<Message>(count);
        for (int i = 0; i < count; i++)
        {
            messages.Add(NewMessage());
        }

        return new ReceiveMessageResponse { Messages = messages };
    }

    private static IAmazonSQS NewClient(Func<ReceiveMessageRequest, CancellationToken, Task<ReceiveMessageResponse>> receive)
    {
        var client = Substitute.For<IAmazonSQS>();
        client.GetQueueUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new GetQueueUrlResponse
            {
                QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/" + ci.ArgAt<string>(0),
            }));
        client.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => receive(ci.Arg<ReceiveMessageRequest>(), ci.Arg<CancellationToken>()));
        client.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeleteMessageResponse()));
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChangeMessageVisibilityBatchResponse()));
        return client;
    }

    private static async Task<ReceiveMessageResponse> YieldThenAsync(
        Func<ReceiveMessageResponse> response, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        return response();
    }

    private static Task<ReceiveMessageResponse> BlockUntilCancelledAsync(CancellationToken cancellationToken)
        => Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(
            _ => new ReceiveMessageResponse(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private static SqsTransportOptions OptionsWith(int maxInFlight, int maxNumberOfMessages = 10) => new()
    {
        AuthMode = SqsAuthMode.DefaultChain,
        WaitTimeSeconds = 0,
        MaxInFlightMessages = maxInFlight,
        MaxNumberOfMessages = maxNumberOfMessages,
    };

    [Fact]
    public async Task ConsumeAsync_RegistryFull_DoesNotReceiveUntilSlotFrees()
    {
        using var cts = new CancellationTokenSource(_guard);
        IAmazonSQS client = NewClient((_, ct) => YieldThenAsync(() => NewBatch(1), ct));
        var adapter = new SqsTransportAdapter(OptionsWith(maxInFlight: 1), NullLogger<SqsTransportAdapter>.Instance, client);

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(QueueName, new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        // The only slot is held by the unsettled message: the loop must not call the broker again.
        await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        await client.Received(1).ReceiveMessageAsync(
            Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());

        // Settling frees the slot and the loop receives again.
        await adapter.SettleAsync(
            SettlementAction.Ack, enumerator.Current, TestContext.Current.CancellationToken);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await client.Received(2).ReceiveMessageAsync(
            Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_IdleAndBusyEndpoints_BusyEndpointKeepsReceivingWithinFairShare()
    {
        using var cts = new CancellationTokenSource(_guard);
        var idleRequests = new List<int>();
        var busyRequests = new List<int>();
        IAmazonSQS client = NewClient((request, ct) =>
        {
            bool idle = request.QueueUrl.EndsWith("idle-queue", StringComparison.Ordinal);
            List<int> sink = idle ? idleRequests : busyRequests;
            lock (sink)
            {
                sink.Add(request.MaxNumberOfMessages ?? 0);
            }

            return idle
                ? YieldThenAsync(() => new ReceiveMessageResponse { Messages = [] }, ct)
                : YieldThenAsync(() => NewBatch(request.MaxNumberOfMessages ?? 0), ct);
        });
        // The registry limit equals one full receive batch, so an unrestricted idle endpoint could pin all of it.
        var adapter = new SqsTransportAdapter(
            OptionsWith(maxInFlight: 10, maxNumberOfMessages: 10), NullLogger<SqsTransportAdapter>.Instance, client);

        await using IAsyncEnumerator<InboundMessage> idleEnumerator =
            adapter.ConsumeAsync("idle-queue", new FlowControlOptions { InternalQueueCapacity = 100 }, cts.Token)
                .GetAsyncEnumerator(cts.Token);
        Task<bool> idleMove = idleEnumerator.MoveNextAsync().AsTask();

        await using IAsyncEnumerator<InboundMessage> busyEnumerator =
            adapter.ConsumeAsync("busy-queue", new FlowControlOptions { InternalQueueCapacity = 100 }, cts.Token)
                .GetAsyncEnumerator(cts.Token);

        const int expected = 20;
        for (int i = 0; i < expected; i++)
        {
            (await busyEnumerator.MoveNextAsync()).Should().BeTrue();
            await adapter.SettleAsync(SettlementAction.Ack, busyEnumerator.Current, TestContext.Current.CancellationToken);
        }

        lock (busyRequests)
        {
            busyRequests.Should().OnlyContain(r => r <= 5, "two active endpoints share a limit of 10");
        }

        lock (idleRequests)
        {
            // The idle endpoint started alone, so only its first request may exceed the fair share.
            idleRequests.Skip(1).Should().OnlyContain(r => r <= 5, "two active endpoints share a limit of 10");
        }

        await cts.CancelAsync();
        // Let the pending MoveNextAsync finish before disposal (disposing mid-move is not supported).
        try
        {
            await idleMove;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task ConsumeAsync_FewerFreeSlotsThanBatch_RequestsOnlyFreeSlots()
    {
        using var cts = new CancellationTokenSource(_guard);
        var requested = new List<int>();
        IAmazonSQS client = NewClient((request, ct) =>
        {
            lock (requested)
            {
                requested.Add(request.MaxNumberOfMessages ?? 0);
            }

            return YieldThenAsync(() => NewBatch(request.MaxNumberOfMessages ?? 0), ct);
        });
        var adapter = new SqsTransportAdapter(
            OptionsWith(maxInFlight: 3, maxNumberOfMessages: 10), NullLogger<SqsTransportAdapter>.Instance, client);

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(QueueName, new FlowControlOptions { InternalQueueCapacity = 100 }, cts.Token)
                .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        lock (requested)
        {
            requested[0].Should().Be(3, "only three registry slots are free");
        }
    }

    [Fact]
    public async Task ConsumeAsync_EndpointBufferAtPrefetchCount_StopsReceiving()
    {
        using var cts = new CancellationTokenSource(_guard);
        var requested = new List<int>();
        IAmazonSQS client = NewClient((request, ct) =>
        {
            lock (requested)
            {
                requested.Add(request.MaxNumberOfMessages ?? 0);
            }

            return YieldThenAsync(() => NewBatch(request.MaxNumberOfMessages ?? 0), ct);
        });
        var adapter = new SqsTransportAdapter(
            OptionsWith(maxInFlight: 100), NullLogger<SqsTransportAdapter>.Instance, client);

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(
                    QueueName,
                    new FlowControlOptions { InternalQueueCapacity = 100, MaxInFlightMessages = 2 },
                    cts.Token)
                .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        // Read exactly one message, then let the loop run: it may only top the buffer up to the prefetch count.
        await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        lock (requested)
        {
            requested.Should().OnlyContain(r => r <= 2, "a request never exceeds the endpoint buffer capacity");
            requested.Sum().Should().BeLessThanOrEqualTo(3, "one message was read, at most two stay buffered");
        }
    }

    [Fact]
    public async Task ConsumeAsync_RegistrationRefused_ReleasesRestOfBatchInOneBatchCall()
    {
        using var cts = new CancellationTokenSource(_guard);
        int call = 0;
        IAmazonSQS client = NewClient((_, ct) => Interlocked.Increment(ref call) == 1
            // The broker hands back three messages although only two slots were requested.
            ? YieldThenAsync(() => new ReceiveMessageResponse { Messages = [NewMessage("rh-1"), NewMessage("rh-2"), NewMessage("rh-3")] }, ct)
            : BlockUntilCancelledAsync(ct));
        var released = new TaskCompletionSource<ChangeMessageVisibilityBatchRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                released.TrySetResult(ci.Arg<ChangeMessageVisibilityBatchRequest>());
                return Task.FromResult(new ChangeMessageVisibilityBatchResponse());
            });
        var adapter = new SqsTransportAdapter(OptionsWith(maxInFlight: 2), NullLogger<SqsTransportAdapter>.Instance, client);

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(QueueName, new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        string first = enumerator.Current.MessageId;
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        string second = enumerator.Current.MessageId;

        ChangeMessageVisibilityBatchRequest request =
            await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        request.Entries.Should().ContainSingle();
        request.Entries[0].ReceiptHandle.Should().Be("rh-3");
        request.Entries[0].VisibilityTimeout.Should().Be(0);
        first.Should().NotBe(second);
        await client.Received(1).ChangeMessageVisibilityBatchAsync(
            Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_ReleaseThrowsNonSdkException_KeepsPolling()
    {
        using var cts = new CancellationTokenSource(_guard);
        var logger = new CapturingLogger();
        int call = 0;
        var secondReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAmazonSQS client = NewClient((_, ct) =>
        {
            int n = Interlocked.Increment(ref call);
            if (n == 1)
            {
                return YieldThenAsync(
                    () => new ReceiveMessageResponse { Messages = [NewMessage("rh-1"), NewMessage("rh-2"), NewMessage("rh-3")] }, ct);
            }

            secondReceive.TrySetResult();
            return BlockUntilCancelledAsync(ct);
        });
        var releaseAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChangeMessageVisibilityBatchResponse>>(_ =>
            {
                releaseAttempted.TrySetResult();
                throw new HttpRequestException("SECRET-EXCEPTION-TEXT");
            });
        var adapter = new SqsTransportAdapter(OptionsWith(maxInFlight: 2), logger, client);

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(QueueName, new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        InboundMessage first = enumerator.Current;
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        InboundMessage second = enumerator.Current;

        await releaseAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Free the slots: the loop, still alive after the failed release, must poll again.
        await adapter.SettleAsync(SettlementAction.Ack, first, TestContext.Current.CancellationToken);
        await adapter.SettleAsync(SettlementAction.Ack, second, TestContext.Current.CancellationToken);
        await secondReceive.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        logger.AllText.Should().NotContain("rh-3").And.NotContain("SECRET-EXCEPTION-TEXT");
    }

    [Fact]
    public async Task ConsumeAsync_ConcurrentEndpoints_NeverReserveMoreThanRegistryLimit()
    {
        const int limit = 5;
        using var cts = new CancellationTokenSource(_guard);
        long held = 0; // slots requested by running receive calls plus unsettled messages
        long maxHeld = 0;

        void Raise(int by)
        {
            long now = Interlocked.Add(ref held, by);
            long seen;
            while (now > (seen = Interlocked.Read(ref maxHeld)))
            {
                Interlocked.CompareExchange(ref maxHeld, now, seen);
            }
        }

        IAmazonSQS client = NewClient(async (request, ct) =>
        {
            int count = request.MaxNumberOfMessages ?? 0;
            Raise(count);
            await Task.Delay(10, ct);
            return NewBatch(count);
        });
        int batchCalls = 0;
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref batchCalls);
                return Task.FromResult(new ChangeMessageVisibilityBatchResponse());
            });
        var adapter = new SqsTransportAdapter(OptionsWith(limit), NullLogger<SqsTransportAdapter>.Instance, client);

        async Task RunEndpointAsync(string name)
        {
            try
            {
                await foreach (InboundMessage message in adapter.ConsumeAsync(
                    name, new FlowControlOptions { InternalQueueCapacity = 100 }, cts.Token))
                {
                    await Task.Delay(5, cts.Token);
                    Interlocked.Decrement(ref held); // before the registry slot is freed
                    await adapter.SettleAsync(SettlementAction.Ack, message, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the run window closes.
            }
        }

        Task[] endpoints = [.. Enumerable.Range(0, 4).Select(i => RunEndpointAsync($"endpoint-{i}"))];

        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        int batchCallsWhileRunning = Volatile.Read(ref batchCalls);
        await cts.CancelAsync();
        await Task.WhenAll(endpoints).WaitAsync(_guard, TestContext.Current.CancellationToken);

        Interlocked.Read(ref maxHeld).Should().BeLessThanOrEqualTo(limit);
        batchCallsWhileRunning.Should().Be(0, "nothing is over-received, so nothing is handed back");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<SqsTransportAdapter>
    {
        private readonly List<string> _entries = [];

        public string AllText
        {
            get
            {
                lock (_entries)
                {
                    return string.Join('\n', _entries);
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
            }
        }
    }
}
