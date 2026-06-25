using System.Buffers;
using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// R8.12 — per-key poison / anti-starvation contract for the ordered dispatch path
/// (ADR-026 §7). Tests drive <see cref="ReceiveEndpointRunner"/> end-to-end via a fake adapter
/// that can simulate persistent handler failures, a controllable <see cref="IDurableParkSettlement"/>,
/// and DLX configuration on the binding.
/// </summary>
public sealed class PoisonContractTests
{
    private const string EndpointName = "poison-test-endpoint";
    private const string DlxName = "test-dlx";
    private const string DlxRoutingKey = "test-dlx-rk";
    private const string PoisonMessageId = "poison-msg-id-1";
    private const string OtherMessageId = "other-msg-id-2";
    // Header used by test adapters to carry original string message IDs into the consumer.
    private const string MsgIdHeader = "test-msg-id";

    // ── Default OFF: MaxDeliveryAttempts == 0 → no parking, R8.7 behavior ────────────────────

    [Fact]
    public async Task MaxDeliveryAttempts_Zero_NeverParks_AllMessagesProcessed()
    {
        // Arrange — MaxDeliveryAttempts = 0 (default OFF). Handler throws for poison message.
        // Contract must NOT park; must NACK and release (lane advances unconditionally).
        const int maxAttempts = 0;

        var durablePark = Substitute.For<IDurableParkSettlement>();
        durablePark
            .ParkHeadDurablyAsync(Arg.Any<InboundMessage>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DurableSettlementResult(true, null)));

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { PoisonMessageId };

        // poison first, then two normal messages on the same lane key
        var adapter = new DurableParkAdapter(
            durablePark,
            [
                (PoisonMessageId, "key-a"),
                (OtherMessageId, "key-a"),
                ($"{OtherMessageId}-2", "key-a"),
            ]);

        EndpointBinding binding = BuildBinding(maxAttempts, DlxName, withDurablePark: true);

        var recorder = new SettlementRecorder(throwingIds);
        // All 3 messages processed: poison NACKed (released), then the 2 normal ones ACKed.
        await RunAsync(binding, adapter, recorder,
            waitForIds: [PoisonMessageId, OtherMessageId, $"{OtherMessageId}-2"]);

        // Park must NEVER be called when MaxDeliveryAttempts == 0.
        await durablePark.DidNotReceive().ParkHeadDurablyAsync(
            Arg.Any<InboundMessage>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        recorder.ProcessedMessageIds.Should().Contain(PoisonMessageId,
            "poison was processed (handler ran, threw, NACK without parking)");
        recorder.ProcessedMessageIds.Should().Contain(OtherMessageId,
            "normal messages must be processed (contract disabled → unconditional release)");
    }

    // ── Park after threshold: ParkHeadDurablyAsync called when attempts >= MaxDeliveryAttempts ─

    [Fact]
    public async Task HeadExceedsMaxDeliveryAttempts_ParkHeadDurablyAsync_Called()
    {
        // Arrange — MaxDeliveryAttempts = 2. Same head is re-delivered twice (adapter sends
        // the same MessageId twice to simulate redelivery). After 2 Nack attempts, park is called.
        const int maxAttempts = 2;

        var durablePark = Substitute.For<IDurableParkSettlement>();
        durablePark
            .ParkHeadDurablyAsync(Arg.Any<InboundMessage>(), DlxName, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DurableSettlementResult(IsDurablyConfirmed: true, FailureReason: null)));

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { PoisonMessageId };
        var adapter = new DurableParkAdapter(
            durablePark,
            [
                (PoisonMessageId, "key-a"),  // delivery #1 → attempt 1 → Nack (< threshold)
                (PoisonMessageId, "key-a"),  // delivery #2 → attempt 2 → PARK (>= threshold)
                (OtherMessageId, "key-a"),   // normal → delivered after park
            ]);

        EndpointBinding binding = BuildBinding(maxAttempts, DlxName, withDurablePark: true);

        var recorder = new SettlementRecorder(throwingIds);
        // Wait until OtherMessageId is processed — that proves park happened and lane released.
        await RunAsync(binding, adapter, recorder, waitForIds: [OtherMessageId]);

        // Park must have been called exactly once — after maxAttempts Nack-deliveries.
        await durablePark.Received(1).ParkHeadDurablyAsync(
            Arg.Is<InboundMessage>(m => m.MessageId == PoisonMessageId),
            DlxName,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ── C3: IsDurablyConfirmed == true → lane released, next message delivered ─────────────

    [Fact]
    public async Task IsDurablyConfirmed_True_LaneReleased_NextMessageDelivered()
    {
        // After durable park confirms, lane MUST advance and process the normal message.
        const int maxAttempts = 1;

        var durablePark = Substitute.For<IDurableParkSettlement>();
        durablePark
            .ParkHeadDurablyAsync(Arg.Any<InboundMessage>(), DlxName, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DurableSettlementResult(IsDurablyConfirmed: true, FailureReason: null)));

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { PoisonMessageId };
        var adapter = new DurableParkAdapter(
            durablePark,
            [
                (PoisonMessageId, "key-a"),
                (OtherMessageId, "key-a"),
            ]);

        EndpointBinding binding = BuildBinding(maxAttempts, DlxName, withDurablePark: true);

        var recorder = new SettlementRecorder(throwingIds);
        await RunAsync(binding, adapter, recorder, waitForIds: [PoisonMessageId, OtherMessageId]);

        recorder.ProcessedMessageIds.Should().Contain(OtherMessageId,
            "after durable park (IsDurablyConfirmed=true), lane must release and process next message");
    }

    // ── C3: IsDurablyConfirmed == false → lane NOT released, next message NOT delivered ─────

    [Fact]
    public async Task IsDurablyConfirmed_False_LaneNotReleased_NextMessageNotDelivered()
    {
        // Park always fails (IsDurablyConfirmed=false). C3: lane MUST NOT advance.
        const int maxAttempts = 1;
        const int retryCount = 1; // 1 park attempt (bound) → exhausted → C3 head stays

        var durablePark = Substitute.For<IDurableParkSettlement>();
        durablePark
            .ParkHeadDurablyAsync(Arg.Any<InboundMessage>(), DlxName, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DurableSettlementResult(IsDurablyConfirmed: false, FailureReason: "simulated broker failure")));

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { PoisonMessageId };
        var adapter = new DurableParkAdapter(
            durablePark,
            [
                (PoisonMessageId, "same-lane-key"),
                (OtherMessageId, "same-lane-key"),
            ]);

        EndpointBinding binding = BuildBinding(maxAttempts, DlxName, withDurablePark: true, retryCount: retryCount);

        var recorder = new SettlementRecorder(throwingIds);
        await RunWithC3AssertionAsync(binding, adapter, recorder, unexpectedId: OtherMessageId);

        // The normal message behind the poison MUST NOT have been dispatched.
        recorder.ProcessedMessageIds.Should().NotContain(OtherMessageId,
            "C3 — lane must NOT release while IsDurablyConfirmed=false; " +
            "next message must NOT be dispatched");
    }

    // ── Fallback NACK→DLX when no IDurableParkSettlement (narrower guarantee) ─────────────

    [Fact]
    public async Task NoDurableParkSettlement_WithDlx_FallbackNack_LaneReleased()
    {
        // Adapter without IDurableParkSettlement; DLX configured.
        // After threshold, lane must fall back to SettleAsync(Nack) and release.
        const int maxAttempts = 1;

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { PoisonMessageId };
        var adapter = new NoDurableParkAdapter(
            [
                (PoisonMessageId, "key-a"),
                (OtherMessageId, "key-a"),
            ]);

        EndpointBinding binding = BuildBinding(maxAttempts, DlxName, withDurablePark: false);

        var recorder = new SettlementRecorder(throwingIds);
        await RunAsync(binding, adapter, recorder, waitForIds: [PoisonMessageId, OtherMessageId]);

        recorder.ProcessedMessageIds.Should().Contain(OtherMessageId,
            "fallback NACK→DLX must release lane so the normal message is delivered");
    }

    // ── No DLX → log lost + Reject + release ─────────────────────────────────────────────

    [Fact]
    public async Task NoDlx_NoDurablePark_Rejects_Releases()
    {
        // No DLX, no IDurableParkSettlement. Must Reject + release (no block-forever).
        const int maxAttempts = 1;

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { PoisonMessageId };
        var adapter = new NoDurableParkAdapter(
            [
                (PoisonMessageId, "key-a"),
                (OtherMessageId, "key-a"),
            ]);

        EndpointBinding binding = BuildBinding(maxAttempts, dlxName: null, withDurablePark: false);

        var recorder = new SettlementRecorder(throwingIds);
        await RunAsync(binding, adapter, recorder, waitForIds: [PoisonMessageId, OtherMessageId]);

        recorder.SettlementActions.Should().Contain(SettlementAction.Reject,
            "no-DLX branch must Reject the poison message");
        recorder.ProcessedMessageIds.Should().Contain(OtherMessageId,
            "no-DLX Reject must release lane — ADR-026 §7 no-block-forever");
    }

    // ── Lane isolation: poison on one lane does NOT block another lane ────────────────────

    [Fact]
    public async Task PoisonLane_DoesNotBlock_OtherLane()
    {
        // 2 lanes: poison on Key-A, normal messages on Key-B. Key-B must complete independently.
        const int maxAttempts = 1;
        const string KeyA = "lane-key-alpha";
        const string KeyB = "lane-key-beta";

        var durablePark = Substitute.For<IDurableParkSettlement>();
        durablePark
            .ParkHeadDurablyAsync(Arg.Any<InboundMessage>(), DlxName, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DurableSettlementResult(true, null)));

        var throwingIds = new HashSet<string>(StringComparer.Ordinal) { $"poison-{KeyA}" };
        var adapter = new DurableParkAdapter(
            durablePark,
            [
                ($"poison-{KeyA}", KeyA),
                ($"normal-{KeyB}-1", KeyB),
                ($"normal-{KeyB}-2", KeyB),
                ($"normal-{KeyB}-3", KeyB),
            ]);

        EndpointBinding binding = BuildBinding(maxAttempts, DlxName, withDurablePark: true, concurrentMessageLimit: 2);

        var recorder = new SettlementRecorder(throwingIds);
        // Wait for all Key-B messages to be processed.
        await RunAsync(binding, adapter, recorder,
            waitForIds: [$"normal-{KeyB}-1", $"normal-{KeyB}-2", $"normal-{KeyB}-3"]);

        recorder.ProcessedMessageIds
            .Where(id => id.StartsWith($"normal-{KeyB}", StringComparison.Ordinal))
            .Should().HaveCount(3,
                "Key-B messages on a separate lane must not be blocked by poison on Key-A");
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────

    private static EndpointBinding BuildBinding(
        int maxDeliveryAttempts,
        string? dlxName,
        bool withDurablePark,
        int concurrentMessageLimit = 1,
        int retryCount = 1)
    {
        _ = withDurablePark; // Controls adapter type, not binding. Binding just has DLX config.

        var ordering = new TestPoisonOrdering(
            headerName: "ordering-key",
            maxDeliveryAttempts: maxDeliveryAttempts);

        return new EndpointBinding
        {
            EndpointName = EndpointName,
            PrefetchCount = 32,
            ConcurrentMessageLimit = concurrentMessageLimit,
            Ordering = ordering,
            RawConsumers = [typeof(ThrowingOrRecordingConsumer)],
            DeadLetterExchange = dlxName,
            DeadLetterRoutingKey = dlxName is not null ? DlxRoutingKey : null,
            RetryCount = retryCount,
            RetryInterval = TimeSpan.FromMilliseconds(10),
        };
    }

    private static Task RunAsync(
        EndpointBinding binding,
        ITransportAdapter adapter,
        SettlementRecorder recorder,
        IReadOnlyList<string> waitForIds)
        => RunCoreAsync(binding, adapter, recorder, waitForIds, TimeSpan.FromSeconds(15));

    private static async Task RunCoreAsync(
        EndpointBinding binding,
        ITransportAdapter adapter,
        SettlementRecorder recorder,
        IReadOnlyList<string> waitForIds,
        TimeSpan timeout)
    {
        WireRecorder(adapter, recorder);

        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<ThrowingOrRecordingConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());

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

        using var cts = new CancellationTokenSource(timeout);
        Task runTask = runner.RunAsync(cts.Token);

        // Wait until all expected IDs have been processed (handler invoked).
        foreach (string id in waitForIds)
        {
            await recorder.WaitForProcessedAsync(id, cts.Token);
        }

        // Small settling window to allow async settlement to complete.
        await Task.Delay(100, CancellationToken.None);

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    private static async Task RunWithC3AssertionAsync(
        EndpointBinding binding,
        ITransportAdapter adapter,
        SettlementRecorder recorder,
        string unexpectedId)
    {
        WireRecorder(adapter, recorder);

        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<ThrowingOrRecordingConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task runTask = runner.RunAsync(cts.Token);

        // Wait for the poison to have been processed at least once.
        await recorder.WaitForProcessedAsync(PoisonMessageId, cts.Token);

        // Give the system a settling window — C3 means the lane is blocked; any
        // spurious advance would show the unexpected ID in ProcessedMessageIds.
        await Task.Delay(400, CancellationToken.None);

        recorder.ProcessedMessageIds.Should().NotContain(unexpectedId,
            $"C3 invariant: lane must NOT advance while IsDurablyConfirmed=false; " +
            $"'{unexpectedId}' must not be dispatched");

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    private static void WireRecorder(ITransportAdapter adapter, SettlementRecorder recorder)
    {
        if (adapter is DurableParkAdapter dpa) dpa.Recorder = recorder;
        else if (adapter is NoDurableParkAdapter npa) npa.Recorder = recorder;
    }

    // ── IConsumerOrderingConfiguration ───────────────────────────────────────────────────

    private sealed class TestPoisonOrdering : IConsumerOrderingConfiguration
    {
        internal TestPoisonOrdering(string? headerName, int maxDeliveryAttempts)
        {
            HeaderName = headerName;
            MaxDeliveryAttempts = maxDeliveryAttempts;
        }

        public string? HeaderName { get; }
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency => null;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public TransportAffinity TransportAffinity => TransportAffinity.None;
        public int MaxDeliveryAttempts { get; }
    }

    // ── Consumer: throws for configured IDs, records all processed IDs ───────────────────

    private sealed class ThrowingOrRecordingConsumer(SettlementRecorder recorder) : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context)
        {
            string msgId = context.Headers.TryGetValue(MsgIdHeader, out string? hdrId)
                ? hdrId
                : context.MessageId.ToString();

            recorder.RecordProcessed(msgId);

            if (recorder.ShouldThrow(msgId))
            {
                throw new InvalidOperationException($"Simulated poison failure for {msgId}");
            }

            return Task.CompletedTask;
        }
    }

    // ── Recorder ─────────────────────────────────────────────────────────────────────────

    private sealed class SettlementRecorder
    {
        private readonly HashSet<string> _throwingIds;
        private readonly ConcurrentBag<string> _processedIds = [];
        private readonly ConcurrentBag<SettlementAction> _actions = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _processedTcs = new();

        internal SettlementRecorder(HashSet<string> throwingIds) => _throwingIds = throwingIds;

        internal IReadOnlyCollection<string> ProcessedMessageIds => _processedIds;
        internal IReadOnlyCollection<SettlementAction> SettlementActions => _actions;

        internal bool ShouldThrow(string messageId) => _throwingIds.Contains(messageId);

        internal void RecordProcessed(string messageId)
        {
            _processedIds.Add(messageId);
            _processedTcs
                .GetOrAdd(messageId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }

        internal void RecordSettled(SettlementAction action) => _actions.Add(action);

        internal async Task WaitForProcessedAsync(string messageId, CancellationToken ct)
        {
            if (_processedIds.Any(id => id == messageId)) return;
            TaskCompletionSource tcs = _processedTcs
                .GetOrAdd(messageId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // ── Adapter WITH IDurableParkSettlement ───────────────────────────────────────────────

    private sealed class DurableParkAdapter(
        IDurableParkSettlement durablePark,
        IReadOnlyList<(string MessageId, string Key)> messages)
        : ITransportAdapter, IDurableParkSettlement
    {
        internal SettlementRecorder? Recorder { get; set; }

        public string TransportName => "DurableParkFake";
        public TransportCapabilities Capabilities => TransportCapabilities.None;

        Task<DurableSettlementResult> IDurableParkSettlement.ParkHeadDurablyAsync(
            InboundMessage message,
            string deadLetterExchange,
            string deadLetterRoutingKey,
            CancellationToken cancellationToken)
            => durablePark.ParkHeadDurablyAsync(message, deadLetterExchange, deadLetterRoutingKey, cancellationToken);

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                var (msgId, key) = messages[i];
                yield return new InboundMessage(
                    messageId: msgId,
                    headers: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ordering-key"] = key,
                        [MsgIdHeader] = msgId,
                    },
                    body: ReadOnlySequence<byte>.Empty,
                    deliveryTag: (ulong)i);
            }

            var tcs = new TaskCompletionSource();
            using (cancellationToken.Register(() => tcs.TrySetResult()))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }

        public Task SettleAsync(
            SettlementAction action,
            InboundMessage message,
            CancellationToken cancellationToken = default)
        {
            Recorder?.RecordSettled(action);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(
            BareWire.Abstractions.Topology.TopologyDeclaration topology,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    // ── Adapter WITHOUT IDurableParkSettlement ────────────────────────────────────────────

    private sealed class NoDurableParkAdapter(IReadOnlyList<(string MessageId, string Key)> messages)
        : ITransportAdapter
    {
        internal SettlementRecorder? Recorder { get; set; }

        public string TransportName => "NoDurableParkFake";
        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                var (msgId, key) = messages[i];
                yield return new InboundMessage(
                    messageId: msgId,
                    headers: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ordering-key"] = key,
                        [MsgIdHeader] = msgId,
                    },
                    body: ReadOnlySequence<byte>.Empty,
                    deliveryTag: (ulong)i);
            }

            var tcs = new TaskCompletionSource();
            using (cancellationToken.Register(() => tcs.TrySetResult()))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }

        public Task SettleAsync(
            SettlementAction action,
            InboundMessage message,
            CancellationToken cancellationToken = default)
        {
            Recorder?.RecordSettled(action);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(
            BareWire.Abstractions.Topology.TopologyDeclaration topology,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
