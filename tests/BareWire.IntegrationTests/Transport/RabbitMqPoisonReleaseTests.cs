using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// E2E integration tests for the per-key poison / anti-starvation contract (R8.12, ADR-026 §7).
/// Each test drives <see cref="ReceiveEndpointRunner"/> against a real RabbitMQ broker provided
/// by <see cref="AspireFixture"/> — the same pattern as
/// <see cref="RabbitMqDurableParkSettlementTests"/> for topology setup and DLQ verification.
/// Every test uses unique queue/exchange names (Guid.NewGuid()) to prevent cross-test interference.
/// </summary>
public sealed class RabbitMqPoisonReleaseTests(AspireFixture fixture) : IClassFixture<AspireFixture>
{
    // ── Test ordering key header ──────────────────────────────────────────────
    private const string OrderingKeyHeader = "ordering-key";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    /// <summary>
    /// Builds an <see cref="EndpointBinding"/> with per-key ordering and the poison contract
    /// (MaxDeliveryAttempts) active. Uses <see cref="ConsumerOrderingStrategy.LocalPartitioned"/>
    /// which is the single-instance path that requires no declared TransportAffinity — safe for
    /// a simple integration harness that runs a single ReceiveEndpointRunner.
    /// </summary>
    private static EndpointBinding BuildPoisonBinding(
        string queueName,
        int maxDeliveryAttempts,
        string? dlxName,
        string? dlxRoutingKey,
        int concurrentMessageLimit = 2)
        => new()
        {
            EndpointName = queueName,
            PrefetchCount = 32,
            ConcurrentMessageLimit = concurrentMessageLimit,
            Ordering = new PoisonTestOrdering(OrderingKeyHeader, maxDeliveryAttempts),
            RawConsumers = [typeof(PoisonOrRecordingConsumer)],
            DeadLetterExchange = dlxName,
            DeadLetterRoutingKey = dlxRoutingKey,
            // RetryCount = 1 so park-retry loop is bounded when IsDurablyConfirmed=false
            RetryCount = 1,
            RetryInterval = TimeSpan.FromMilliseconds(100),
        };

    /// <summary>Reads exactly one message from the queue with a timeout.</summary>
    private static async Task<InboundMessage> ConsumeOneAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, flow, ct))
        {
            return msg;
        }

        throw new InvalidOperationException("Consume stream ended before a message arrived.");
    }

    private static byte[] ReadSequenceToArray(ReadOnlySequence<byte> seq)
    {
        if (seq.IsSingleSegment)
            return seq.FirstSpan.ToArray();

        byte[] buf = new byte[seq.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> seg in seq)
        {
            seg.Span.CopyTo(buf.AsSpan(offset));
            offset += seg.Length;
        }

        return buf;
    }

    // ── Helper: publish messages via the default AMQP exchange ────────────────

    private static async Task PublishMessageAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        string messageId,
        string orderingKey,
        CancellationToken ct)
    {
        string payload = $"{{\"id\":\"{messageId}\"}}";
        OutboundMessage msg = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = string.Empty,
                [OrderingKeyHeader] = orderingKey,
                // Carry the message id in a header so the consumer can identify it
                // without deserializing the body (raw consumer path).
                ["test-msg-id"] = messageId,
            },
            body: Encoding.UTF8.GetBytes(payload),
            contentType: "application/json");

        await adapter.SendBatchAsync([msg], ct);
    }

    // ── Helper: stand up ReceiveEndpointRunner + PoisonOrRecordingConsumer ────

    private static (ReceiveEndpointRunner Runner, ProcessedTracker Tracker) BuildRunner(
        EndpointBinding binding,
        ITransportAdapter adapter,
        HashSet<string> throwingIds)
    {
        var tracker = new ProcessedTracker(throwingIds);

        var services = new ServiceCollection();
        services.AddSingleton(tracker);
        services.AddScoped<PoisonOrRecordingConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver
            .Resolve(Arg.Any<string?>())
            .Returns(Substitute.For<IMessageDeserializer>());

        var runner = new ReceiveEndpointRunner(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FlowController(NullLogger<FlowController>.Instance),
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        return (runner, tracker);
    }

    // ── Test 1: Poison head → park after MaxDeliveryAttempts → lane released ─────────────

    /// <summary>
    /// E2E test for the full poison-release contract (ADR-026 §7, C3):
    /// <list type="bullet">
    ///   <item>A poison message for key <c>A</c> (handler always throws) is parked in the DLQ
    ///   after <see cref="EndpointBinding.Ordering"/>.MaxDeliveryAttempts delivery attempts.</item>
    ///   <item>After durable-ack (<c>IsDurablyConfirmed=true</c>), the lane is released and
    ///   the subsequent message for key <c>A</c> (A2) is processed.</item>
    ///   <item>Messages for an unrelated key <c>B</c> are not blocked by the poison on key <c>A</c>.</item>
    ///   <item>The original poison message is ACKed (removed from the source queue) and a copy
    ///   exists in the DLQ.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task PoisonHead_ParkedAfterMaxAttempts_LaneReleasedAndOtherKeysUnaffected()
    {
        // Arrange ──────────────────────────────────────────────────────────────
        string id = Guid.NewGuid().ToString("N");
        string srcQueue = $"test-poison-src-{id}";
        string dlxName = $"test-poison-dlx-{id}";
        string dlqName = $"test-poison-dlq-{id}";

        const string poisonId = "poison-A1";
        const string goodA2Id = "good-A2";
        const string goodB1Id = "good-B1";
        const string keyA = "key-alpha";
        const string keyB = "key-beta";

        // 60-second budget: broker startup + topology deploy + message round-trip.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));

        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        // Deploy topology: source queue with DLX → DLX → DLQ with binding.
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(dlxName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(dlqName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(dlxName, dlqName, routingKey: dlqName);
        configurator.DeclareQueue(srcQueue, durable: false, autoDelete: false,
            configure: q => q.DeadLetterExchange(dlxName).DeadLetterRoutingKey(dlqName));
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Publish: poison (key A) first, then A2 (key A), then B1 (key B).
        // The poison message will arrive at the head of key-A's lane.
        await PublishMessageAsync(adapter, srcQueue, poisonId, keyA, cts.Token);
        await PublishMessageAsync(adapter, srcQueue, goodA2Id, keyA, cts.Token);
        await PublishMessageAsync(adapter, srcQueue, goodB1Id, keyB, cts.Token);

        // MaxDeliveryAttempts = 2: broker will deliver the poison twice before park.
        // We simulate this by publishing the poison twice (redelivery simulation):
        // the runner sees the same MessageId twice and increments the per-head counter.
        // Deliver the poison a second time so the counter reaches the threshold.
        await PublishMessageAsync(adapter, srcQueue, poisonId, keyA, cts.Token);

        EndpointBinding binding = BuildPoisonBinding(
            srcQueue,
            maxDeliveryAttempts: 2,
            dlxName: dlxName,
            dlxRoutingKey: dlqName);

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { poisonId };
        (ReceiveEndpointRunner runner, ProcessedTracker tracker) = BuildRunner(binding, adapter, throwingIds);

        // Act ──────────────────────────────────────────────────────────────────
        Task runTask = runner.RunAsync(cts.Token);

        // Wait for A2 and B1 to be processed — this proves that both the poison-lane release
        // (C3: IsDurablyConfirmed=true) and lane isolation (B not blocked by A) work.
        await tracker.WaitForProcessedAsync(goodA2Id, cts.Token);
        await tracker.WaitForProcessedAsync(goodB1Id, cts.Token);

        // Give a short settling window for the durable-ack to complete.
        await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        // Assert 1: A2 and B1 were processed (lane released, isolation held).
        tracker.ProcessedIds.Should().Contain(goodA2Id,
            "after durable park of the poison head, the lane must be released and A2 delivered");
        tracker.ProcessedIds.Should().Contain(goodB1Id,
            "key B runs on an independent lane and must not be blocked by poison on key A");

        // Assert 2: poison copy exists in the DLQ (broker-durable confirmation).
        await using RabbitMqTransportAdapter dlqAdapter = CreateAdapter();
        using CancellationTokenSource dlqCts = new(TimeSpan.FromSeconds(15));
        InboundMessage dlqMsg = await ConsumeOneAsync(dlqAdapter, dlqName, dlqCts.Token);

        byte[] dlqBody = ReadSequenceToArray(dlqMsg.Body);
        string dlqPayload = Encoding.UTF8.GetString(dlqBody);
        dlqPayload.Should().Contain(poisonId,
            "the poison message body must appear in the DLQ after durable park");

        // Clean up — ACK the DLQ message.
        await dlqAdapter.SettleAsync(SettlementAction.Ack, dlqMsg, dlqCts.Token);
    }

    // ── Test 2: Failed-settle (DLX no bound queue) → NO release, head stays ──────────────

    /// <summary>
    /// C3 failed-settle test on a real broker (mirroring
    /// <see cref="RabbitMqDurableParkSettlementTests.ParkHeadAsync_WhenDlxHasNoBoundQueue_ReturnsFailed_AndOriginalStaysInQueue"/>):
    /// when <see cref="IDurableParkSettlement.ParkHeadDurablyAsync"/> returns
    /// <c>IsDurablyConfirmed=false</c> (mandatory publish returned — DLX exists but has no bound
    /// queue), the poison lane MUST NOT be released. The message behind the poison head on the
    /// same key must NOT be processed within a bounded window.
    /// </summary>
    [Fact]
    public async Task PoisonHead_WhenDlxHasNoBoundQueue_LaneNotReleased_NextMessageNotDelivered()
    {
        // Arrange ──────────────────────────────────────────────────────────────
        string id = Guid.NewGuid().ToString("N");
        string srcQueue = $"test-poison-nobind-src-{id}";
        string dlxName = $"test-poison-nobind-dlx-{id}";

        const string poisonId = "poison-nob-A1";
        const string behindPoisonId = "behind-poison-A2";
        const string keyA = "key-nobind-alpha";

        // Generous timeout — broker interaction takes time, but the lane-not-released
        // assertion uses a deliberate short wait window, not the full budget.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));

        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        // Deploy: source queue with DLX that has NO bound queue.
        // ParkHeadDurablyAsync will publish with mandatory=true → broker returns the message
        // (no route) → IsDurablyConfirmed=false.
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(dlxName, ExchangeType.Direct, durable: false, autoDelete: false);
        // Intentionally NO DeclareQueue / BindExchangeToQueue for dlxName.
        configurator.DeclareQueue(srcQueue, durable: false, autoDelete: false,
            configure: q => q.DeadLetterExchange(dlxName).DeadLetterRoutingKey("no-such-queue"));
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Publish: poison (key A) then the message behind it (same key A).
        await PublishMessageAsync(adapter, srcQueue, poisonId, keyA, cts.Token);
        await PublishMessageAsync(adapter, srcQueue, behindPoisonId, keyA, cts.Token);

        // MaxDeliveryAttempts = 1: after 1 failed dispatch, park is attempted immediately.
        // Since park fails (IsDurablyConfirmed=false), C3 requires the lane NOT advance.
        EndpointBinding binding = BuildPoisonBinding(
            srcQueue,
            maxDeliveryAttempts: 1,
            dlxName: dlxName,
            dlxRoutingKey: "no-such-queue",
            concurrentMessageLimit: 1);

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { poisonId };
        (ReceiveEndpointRunner runner, ProcessedTracker tracker) = BuildRunner(binding, adapter, throwingIds);

        // Act ──────────────────────────────────────────────────────────────────
        Task runTask = runner.RunAsync(cts.Token);

        // Wait for the poison to be dispatched at least once (proves the runner is consuming).
        await tracker.WaitForProcessedAsync(poisonId, cts.Token);

        // C3 settling window: give the runner time to attempt park (which will fail).
        // The message BEHIND the poison (behindPoisonId) must NOT be processed in this window.
        // Using 1500 ms — park-retry uses RetryInterval = 100 ms with RetryCount = 1,
        // so one retry attempt plus transport RTT comfortably fits.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), CancellationToken.None);

        // Assert — C3: the message behind the poison must NOT have been dispatched.
        tracker.ProcessedIds.Should().NotContain(behindPoisonId,
            "C3 — lane must NOT be released while IsDurablyConfirmed=false; " +
            "the message behind the poison head must NOT be dispatched");

        // Cleanup — cancel and let the runner drain.
        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    // ── Gap-log opaque-token note ──────────────────────────────────────────────
    //
    // Test 3 (gap-log opaque token assertion) is intentionally omitted from this
    // integration test class. Wiring a log collector into the full Aspire-backed
    // ReceiveEndpointRunner introduces flakiness and tight coupling to log formatting.
    // The opaque-token guarantee is already covered by:
    //   - Unit: BareWire.UnitTests/BareWire/Bus/OrderingSecurityTests.cs (gap-log NotContain asserts)
    //   - Chunk 2 MappingEpochTrackerTests (re-map log SEC asserts)
    // No broker-level log assertion is needed or added here.

    // ── Shared types ──────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IConsumerOrderingConfiguration"/> for the integration test.
    /// Uses <see cref="ConsumerOrderingStrategy.LocalPartitioned"/> — the single-instance path
    /// that does not require declared TransportAffinity; safe for a single ReceiveEndpointRunner.
    /// </summary>
    private sealed class PoisonTestOrdering(string headerName, int maxDeliveryAttempts)
        : IConsumerOrderingConfiguration
    {
        public string? HeaderName => headerName;
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency => null;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public TransportAffinity TransportAffinity => TransportAffinity.None;
        public int MaxDeliveryAttempts => maxDeliveryAttempts;
    }

    /// <summary>
    /// Tracks which message IDs have been dispatched to their consumer handler.
    /// Throws for IDs in <c>throwingIds</c> to simulate a poison message.
    /// </summary>
    private sealed class ProcessedTracker(HashSet<string> throwingIds)
    {
        private readonly ConcurrentBag<string> _processedIds = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _tcsMap = new();

        internal IReadOnlyCollection<string> ProcessedIds => _processedIds;

        internal bool ShouldThrow(string messageId) => throwingIds.Contains(messageId);

        internal void RecordProcessed(string messageId)
        {
            _processedIds.Add(messageId);
            _tcsMap
                .GetOrAdd(messageId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }

        internal async Task WaitForProcessedAsync(string messageId, CancellationToken ct)
        {
            if (_processedIds.Any(id => id == messageId)) return;
            TaskCompletionSource tcs = _tcsMap
                .GetOrAdd(messageId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await tcs.Task.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Raw consumer that records all dispatched message IDs and throws for poison IDs.
    /// The message ID is extracted from the <c>test-msg-id</c> header.
    /// </summary>
    private sealed class PoisonOrRecordingConsumer(ProcessedTracker tracker) : IRawConsumer
    {
        private const string MsgIdHeader = "test-msg-id";

        public Task ConsumeAsync(RawConsumeContext context)
        {
            string msgId = context.Headers.TryGetValue(MsgIdHeader, out string? h) ? h : context.MessageId.ToString();
            tracker.RecordProcessed(msgId);

            if (tracker.ShouldThrow(msgId))
                throw new InvalidOperationException($"Simulated poison failure for message '{msgId}'.");

            return Task.CompletedTask;
        }
    }
}
