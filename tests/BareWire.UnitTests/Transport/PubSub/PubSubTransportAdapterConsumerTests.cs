using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Google.PubSub.Internal;
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

    // ── Shared in-flight registry across concurrent endpoints ────────────────

    [Fact]
    public async Task ConsumeAsync_TwoConcurrentEndpoints_ShareSingleInFlightRegistryAndBothSettle()
    {
        // The registry is created eagerly in the constructor, so concurrent ConsumeAsync calls (one per
        // receive endpoint) cannot race on a lazy initialization and register into an abandoned instance.
        var (adapter, _, subscriber) = CreateAdapterWithMocks();

        var served = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        subscriber.PullAsync(
                Arg.Any<SubscriptionName>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                string subscription = callInfo.Arg<SubscriptionName>().SubscriptionId;
                if (served.AddOrUpdate(subscription, 1, static (_, n) => n + 1) == 1)
                {
                    return Task.FromResult(BuildPullResponse(ackId: "ack-" + subscription, body: []));
                }

                throw new OperationCanceledException();
            });

        subscriber.AcknowledgeAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        PubSubInFlightRegistry registryBefore = adapter.InFlightRegistry;

        async Task<InboundMessage> ConsumeFirstAsync(string subscription)
        {
            using var cts = new CancellationTokenSource();
            await foreach (InboundMessage msg in adapter.ConsumeAsync(
                subscription, new FlowControlOptions { InternalQueueCapacity = 10 }, cts.Token))
            {
                cts.Cancel();
                return msg;
            }

            throw new InvalidOperationException("No message was received.");
        }

        InboundMessage[] received = await Task.WhenAll(ConsumeFirstAsync("sub-a"), ConsumeFirstAsync("sub-b"));

        adapter.InFlightRegistry.Should().BeSameAs(registryBefore);
        foreach (InboundMessage message in received)
        {
            await adapter.SettleAsync(SettlementAction.Ack, message, TestContext.Current.CancellationToken);
        }

        await subscriber.Received(1).AcknowledgeAsync(
            Arg.Any<string>(),
            Arg.Is<IEnumerable<string>>(ids => ids.Contains("ack-sub-a")),
            Arg.Any<CancellationToken>());
        await subscriber.Received(1).AcknowledgeAsync(
            Arg.Any<string>(),
            Arg.Is<IEnumerable<string>>(ids => ids.Contains("ack-sub-b")),
            Arg.Any<CancellationToken>());
    }

    // ── Slot reservation, endpoint buffer cap and batch return ───────────────
    //
    // The harness yields on every pull (an async substitute), because the polling loop starts synchronously
    // on the first MoveNextAsync: a substitute returning an already-completed task would spin a non-awaiting
    // loop forever instead of failing the test. Every test is bounded by a 10 s token.

    private static readonly TimeSpan _guard = TimeSpan.FromSeconds(10);

    private static int _messageSeed;

    private static ReceivedMessage NewMessage(string? ackId = null)
    {
        int n = Interlocked.Increment(ref _messageSeed);
        return new ReceivedMessage
        {
            AckId = ackId ?? $"ack-{n}",
            Message = new PubsubMessage { MessageId = $"msg-{n}", Data = ByteString.Empty },
        };
    }

    private static PullResponse NewBatch(int count)
    {
        var response = new PullResponse();
        for (int i = 0; i < count; i++)
        {
            response.ReceivedMessages.Add(NewMessage());
        }

        return response;
    }

    private static PullResponse NewBatch(params ReceivedMessage[] messages)
    {
        var response = new PullResponse();
        response.ReceivedMessages.AddRange(messages);
        return response;
    }

    private static async Task<PullResponse> YieldThenAsync(Func<PullResponse> response, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        return response();
    }

    private static Task<PullResponse> BlockUntilCancelledAsync(CancellationToken cancellationToken)
        => Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(
            _ => new PullResponse(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private static (PubSubTransportAdapter Adapter, SubscriberServiceApiClient Subscriber) NewAdapter(
        int maxInFlight,
        Func<int, CancellationToken, Task<PullResponse>> pull,
        Microsoft.Extensions.Logging.ILogger<PubSubTransportAdapter>? logger = null)
    {
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        subscriber.PullAsync(Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => pull(ci.ArgAt<int>(1), ci.Arg<CancellationToken>()));
        subscriber.AcknowledgeAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ApplicationDefault,
            ProjectId = "test-project",
            MaxInFlightMessages = maxInFlight,
            MaxOutstandingMessages = 10,
        };
        var adapter = new PubSubTransportAdapter(
            options, logger ?? NullLogger<PubSubTransportAdapter>.Instance, publisher, subscriber);
        return (adapter, subscriber);
    }

    [Fact]
    public async Task ConsumeAsync_RegistryFull_DoesNotPullUntilSlotFrees()
    {
        using var cts = new CancellationTokenSource(_guard);
        var (adapter, subscriber) = NewAdapter(1, (_, ct) => YieldThenAsync(() => NewBatch(1), ct));

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        // The only slot is held by the unsettled message: the loop must not call the broker again.
        await Task.Delay(TimeSpan.FromMilliseconds(400), TestContext.Current.CancellationToken);

        await subscriber.Received(1).PullAsync(
            Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Settling frees the slot and the loop pulls again.
        await adapter.SettleAsync(
            SettlementAction.Ack, enumerator.Current, TestContext.Current.CancellationToken);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await subscriber.Received(2).PullAsync(
            Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_IdleAndBusyEndpoints_BusyEndpointKeepsReceivingWithinFairShare()
    {
        using var cts = new CancellationTokenSource(_guard);
        var idleRequests = new List<int>();
        var busyRequests = new List<int>();
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        subscriber.PullAsync(Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                bool idle = ci.Arg<SubscriptionName>().SubscriptionId == "idle-sub";
                int max = ci.ArgAt<int>(1);
                List<int> sink = idle ? idleRequests : busyRequests;
                lock (sink)
                {
                    sink.Add(max);
                }

                return idle
                    ? YieldThenAsync(() => new PullResponse(), ci.Arg<CancellationToken>())
                    : YieldThenAsync(() => NewBatch(max), ci.Arg<CancellationToken>());
            });
        subscriber.AcknowledgeAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Default-like limits: registry 100, prefetch 100, broker batch 1000 - one unrestricted endpoint takes it all.
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ApplicationDefault,
            ProjectId = "test-project",
            MaxInFlightMessages = 100,
            MaxOutstandingMessages = 1000,
        };
        var adapter = new PubSubTransportAdapter(
            options, NullLogger<PubSubTransportAdapter>.Instance, publisher, subscriber);
        var flow = new FlowControlOptions { InternalQueueCapacity = 1000, MaxInFlightMessages = 100 };

        await using IAsyncEnumerator<InboundMessage> idleEnumerator =
            adapter.ConsumeAsync("idle-sub", flow, cts.Token).GetAsyncEnumerator(cts.Token);
        Task<bool> idleMove = idleEnumerator.MoveNextAsync().AsTask();

        await using IAsyncEnumerator<InboundMessage> busyEnumerator =
            adapter.ConsumeAsync("busy-sub", flow, cts.Token).GetAsyncEnumerator(cts.Token);

        const int expected = 120;
        for (int i = 0; i < expected; i++)
        {
            (await busyEnumerator.MoveNextAsync()).Should().BeTrue();
            await adapter.SettleAsync(SettlementAction.Ack, busyEnumerator.Current, TestContext.Current.CancellationToken);
        }

        lock (busyRequests)
        {
            busyRequests.Should().OnlyContain(r => r <= 50, "two active endpoints share a limit of 100");
        }

        lock (idleRequests)
        {
            // The idle endpoint started alone, so only its first request may exceed the fair share.
            idleRequests.Skip(1).Should().OnlyContain(r => r <= 50, "two active endpoints share a limit of 100");
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
        var (adapter, subscriber) = NewAdapter(3, (max, ct) => YieldThenAsync(() => NewBatch(max), ct));

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, new FlowControlOptions { InternalQueueCapacity = 100 }, cts.Token)
                .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        await subscriber.Received(1).PullAsync(
            Arg.Any<SubscriptionName>(),
            Arg.Is<int>(m => m == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_EndpointBufferAtPrefetchCount_StopsPulling()
    {
        using var cts = new CancellationTokenSource(_guard);
        var requested = new List<int>();
        var (adapter, _) = NewAdapter(100, (max, ct) =>
        {
            lock (requested)
            {
                requested.Add(max);
            }

            return YieldThenAsync(() => NewBatch(max), ct);
        });

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(
                    SubscriptionId,
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
    public async Task ConsumeAsync_RegistrationRefused_ReleasesRestOfBatchInOneCall()
    {
        using var cts = new CancellationTokenSource(_guard);
        int call = 0;
        var (adapter, subscriber) = NewAdapter(2, (_, ct) => Interlocked.Increment(ref call) == 1
            // The broker hands back three messages although only two slots were requested.
            ? YieldThenAsync(() => NewBatch(NewMessage("ack-1"), NewMessage("ack-2"), NewMessage("ack-3")), ct)
            : BlockUntilCancelledAsync(ct));
        var released = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                released.TrySetResult([.. ci.Arg<IEnumerable<string>>()]);
                return Task.CompletedTask;
            });

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        string[] ackIds = await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        ackIds.Should().Equal("ack-3");
        await subscriber.Received(1).ModifyAckDeadlineAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Is<int>(d => d == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_ReleaseThrowsNonRpcException_KeepsPulling()
    {
        using var cts = new CancellationTokenSource(_guard);
        var logger = new CapturingLogger();
        int call = 0;
        var secondPull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (adapter, subscriber) = NewAdapter(2, (_, ct) =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                return YieldThenAsync(() => NewBatch(NewMessage("ack-1"), NewMessage("ack-2"), NewMessage("ack-3")), ct);
            }

            secondPull.TrySetResult();
            return BlockUntilCancelledAsync(ct);
        }, logger);
        var releaseAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ =>
            {
                releaseAttempted.TrySetResult();
                throw new HttpRequestException("SECRET-EXCEPTION-TEXT");
            });

        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        InboundMessage first = enumerator.Current;
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        InboundMessage second = enumerator.Current;

        await releaseAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Free the slots: the loop, still alive after the failed release, must pull again.
        await adapter.SettleAsync(SettlementAction.Ack, first, TestContext.Current.CancellationToken);
        await adapter.SettleAsync(SettlementAction.Ack, second, TestContext.Current.CancellationToken);
        await secondPull.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        logger.AllText.Should().NotContain("ack-3").And.NotContain("SECRET-EXCEPTION-TEXT");
    }

    [Fact]
    public async Task ConsumeAsync_ConcurrentEndpoints_NeverReserveMoreThanRegistryLimit()
    {
        const int limit = 5;
        using var cts = new CancellationTokenSource(_guard);
        long held = 0; // slots requested by running pulls plus unsettled messages
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

        var (adapter, subscriber) = NewAdapter(limit, async (max, ct) =>
        {
            Raise(max);
            await Task.Delay(10, ct);
            return NewBatch(max);
        });
        int releaseCalls = 0;
        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref releaseCalls);
                return Task.CompletedTask;
            });

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
        int releasesWhileRunning = Volatile.Read(ref releaseCalls);
        await cts.CancelAsync();
        await Task.WhenAll(endpoints).WaitAsync(_guard, TestContext.Current.CancellationToken);

        Interlocked.Read(ref maxHeld).Should().BeLessThanOrEqualTo(limit);
        releasesWhileRunning.Should().Be(0, "nothing is over-pulled, so nothing is handed back");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<PubSubTransportAdapter>
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
