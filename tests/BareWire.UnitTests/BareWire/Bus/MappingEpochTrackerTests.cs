using System.Buffers;
using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// R8.12 C4 — consume-side re-map detection via <see cref="MappingEpochTracker"/>.
/// Guards: first observation = no log; epoch change = Warning emitted; same epoch = no log;
/// absent header = no-op; bound = N fixed lane entries (not per-key growth).
/// Also includes the SEC-1 cross-check: Core's literal matches the RabbitMQ adapter's literal.
/// </summary>
public sealed class MappingEpochTrackerTests
{
    // ── SEC-1: Cross-check literal identity ──────────────────────────────────────────────────────

    /// <summary>
    /// Ensures that <see cref="MappingEpochTracker.MappingEpochHeaderName"/> and
    /// <c>RabbitMqTransportAdapter.MappingEpochHeaderName</c> are IDENTICAL strings.
    /// A divergence would silently disable re-map detection (SEC-1 / OQ-2).
    /// </summary>
    [Fact]
    public void MappingEpochHeaderName_CoreAndRabbitMqAdapter_AreIdentical()
    {
        MappingEpochTracker.MappingEpochHeaderName.Should().Be(
            RabbitMqTransportAdapter.MappingEpochHeaderName,
            "Core and the RabbitMQ adapter MUST use the identical header name literal; " +
            "a divergence silently disables re-map detection (SEC-1, OQ-2)");
    }

    // ── Unit tests for MappingEpochTracker.Observe ────────────────────────────────────────────────

    [Fact]
    public void Observe_FirstObservation_EmitsNoLog()
    {
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 2, endpointName: "ep", logger: logger);

        tracker.Observe(laneIndex: 0, epoch: 100L, orderingKey: "key-a");

        logger.WarningCount.Should().Be(0,
            "the first epoch observation for a lane must be stored silently (no re-map window yet)");
    }

    [Fact]
    public void Observe_SameEpochTwice_EmitsNoLog()
    {
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 2, endpointName: "ep", logger: logger);

        tracker.Observe(laneIndex: 0, epoch: 42L, orderingKey: "key-a");
        tracker.Observe(laneIndex: 0, epoch: 42L, orderingKey: "key-a");

        logger.WarningCount.Should().Be(0,
            "repeated observations of the same epoch must not emit a log (no change = no re-map)");
    }

    [Fact]
    public void Observe_EpochChange_EmitsWarningLog()
    {
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 2, endpointName: "ep", logger: logger);

        tracker.Observe(laneIndex: 0, epoch: 100L, orderingKey: "key-a");
        tracker.Observe(laneIndex: 0, epoch: 200L, orderingKey: "key-a"); // epoch changed

        logger.WarningCount.Should().Be(1,
            "an epoch change on a lane must emit exactly one re-map window Warning");
    }

    [Fact]
    public void Observe_EpochChange_LogMessageContainsEndpointName()
    {
        const string EndpointName = "my-endpoint";
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 1, endpointName: EndpointName, logger: logger);

        tracker.Observe(laneIndex: 0, epoch: 1L, orderingKey: "key-x");
        tracker.Observe(laneIndex: 0, epoch: 2L, orderingKey: "key-x");

        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain(EndpointName,
                "the re-map warning must identify the endpoint");
    }

    [Fact]
    public void Observe_EpochChange_LogMessageDoesNotContainRawKey()
    {
        const string SecretKey = "acct-secret-456";
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 1, endpointName: "ep", logger: logger);

        tracker.Observe(laneIndex: 0, epoch: 1L, orderingKey: SecretKey);
        tracker.Observe(laneIndex: 0, epoch: 2L, orderingKey: SecretKey);

        logger.Warnings.Should().ContainSingle()
            .Which.Should().NotContain(SecretKey,
                "the raw ordering-key must NEVER appear in the re-map log (S2 — ADR-026 §NIE WOLNO)");
    }

    [Fact]
    public void Observe_EpochChange_LogMessageContainsOpaqueToken()
    {
        const string Key = "customer-789";
        string expectedToken = OrderingKeyDiagnostics.ToOpaqueToken(Key);
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 1, endpointName: "ep", logger: logger);

        tracker.Observe(laneIndex: 0, epoch: 1L, orderingKey: Key);
        tracker.Observe(laneIndex: 0, epoch: 2L, orderingKey: Key);

        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain(expectedToken,
                "the re-map warning must include the opaque token for log correlation (S2)");
    }

    [Fact]
    public void Observe_NullKey_EpochChange_LogsLaneIndex()
    {
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 4, endpointName: "ep", logger: logger);

        tracker.Observe(laneIndex: 2, epoch: 5L, orderingKey: null);
        tracker.Observe(laneIndex: 2, epoch: 6L, orderingKey: null);

        logger.WarningCount.Should().Be(1, "epoch change with null key still emits a warning");
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("lane:2",
                "when key is null the lane-index aggregate must appear in the log (not a raw key)");
    }

    [Fact]
    public void Observe_MultipleEpochChanges_EmitsOneWarningPerChange()
    {
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 1, endpointName: "ep", logger: logger);

        tracker.Observe(laneIndex: 0, epoch: 1L, orderingKey: "k");
        tracker.Observe(laneIndex: 0, epoch: 2L, orderingKey: "k"); // change 1
        tracker.Observe(laneIndex: 0, epoch: 2L, orderingKey: "k"); // same — no log
        tracker.Observe(laneIndex: 0, epoch: 3L, orderingKey: "k"); // change 2

        logger.WarningCount.Should().Be(2,
            "each distinct epoch change on a lane must emit exactly one warning");
    }

    [Fact]
    public void Observe_DifferentLanes_TrackedIndependently()
    {
        var logger = new FakeLogger();
        var tracker = new MappingEpochTracker(laneCount: 3, endpointName: "ep", logger: logger);

        // Lane 0: epoch change → 1 warning
        tracker.Observe(laneIndex: 0, epoch: 10L, orderingKey: "k0");
        tracker.Observe(laneIndex: 0, epoch: 20L, orderingKey: "k0");

        // Lane 1: same epoch twice → 0 warnings
        tracker.Observe(laneIndex: 1, epoch: 10L, orderingKey: "k1");
        tracker.Observe(laneIndex: 1, epoch: 10L, orderingKey: "k1");

        // Lane 2: never observed → 0 warnings
        // Total: exactly 1 warning (from lane 0)
        logger.WarningCount.Should().Be(1,
            "each lane has its own independent epoch tracking — changes on one lane do not affect others");
    }

    // ── Bound: N fixed entries (not per-key growth) ───────────────────────────────────────────────

    [Fact]
    public void Observe_ManyDifferentKeys_OnSameLane_DoesNotGrow()
    {
        // Per OQ-6 decision: tracking is per-LANE, so any number of distinct keys on the same lane
        // does not increase memory. The tracker stays bounded at laneCount entries.
        var logger = new FakeLogger();
        const int LaneCount = 2;
        var tracker = new MappingEpochTracker(laneCount: LaneCount, endpointName: "ep", logger: logger);

        // Feed 1000 different key names into lane 0 — all with the same epoch (no change expected).
        for (int i = 0; i < 1000; i++)
        {
            tracker.Observe(laneIndex: 0, epoch: 99L, orderingKey: $"key-{i}");
        }

        // Only the very first call counts as an observation; all subsequent with same epoch = no-op.
        logger.WarningCount.Should().Be(0,
            "per-lane tracking: many keys on the same lane with the same epoch must not emit warnings");
    }

    // ── Integration: absent BW-MappingEpoch header → tracker not called → no log ─────────────────

    /// <summary>
    /// End-to-end: a <see cref="ReceiveEndpointRunner"/> with ordered dispatch processes messages
    /// that carry no <c>BW-MappingEpoch</c> header. The tracker must remain a no-op (D2).
    /// </summary>
    [Fact]
    public async Task OrderedRunner_NoEpochHeader_NoRemapLog()
    {
        const string EndpointName = "no-epoch-ep";
        const string MsgId = "msg-no-epoch";
        const string Key = "key-a";

        var captureLogger = new FakeLogger();
        var adapter = new SimpleAdapter(
            [(MsgId, Key, epochHeader: null)]);

        EndpointBinding binding = BuildBinding(EndpointName);

        var recorder = new Recorder();
        await RunAsync(binding, adapter, recorder, captureLogger, waitForIds: [MsgId]);

        captureLogger.WarningCount.Should().Be(0,
            "absent BW-MappingEpoch header → tracker is a no-op (D2 from R8.9 — no stamp = no detection)");
    }

    /// <summary>
    /// End-to-end: messages with an epoch header are processed — first message no log, second message
    /// with same epoch no log, third message with different epoch emits one Warning.
    /// </summary>
    [Fact]
    public async Task OrderedRunner_EpochChange_EmitsRemapWarning()
    {
        const string EndpointName = "epoch-ep";
        const string Key = "key-a";

        var captureLogger = new FakeLogger();
        var adapter = new SimpleAdapter(
        [
            ("msg-1", Key, epochHeader: "100"),  // first → store, no log
            ("msg-2", Key, epochHeader: "100"),  // same  → no log
            ("msg-3", Key, epochHeader: "200"),  // change → Warning
        ]);

        EndpointBinding binding = BuildBinding(EndpointName);
        var recorder = new Recorder();
        await RunAsync(binding, adapter, recorder, captureLogger, waitForIds: ["msg-3"]);

        captureLogger.WarningCount.Should().Be(1,
            "epoch change from 100→200 on the same lane must produce exactly one re-map window Warning");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────────

    private static EndpointBinding BuildBinding(string endpointName) => new()
    {
        EndpointName = endpointName,
        PrefetchCount = 32,
        ConcurrentMessageLimit = 1,
        Ordering = new TestOrdering(),
        RawConsumers = [typeof(RecordingConsumer)],
    };

    private static Task RunAsync(
        EndpointBinding binding,
        ITransportAdapter adapter,
        Recorder recorder,
        ILogger runnerLogger,
        IReadOnlyList<string> waitForIds)
        => RunCoreAsync(binding, adapter, recorder, runnerLogger, waitForIds, TimeSpan.FromSeconds(15));

    private static async Task RunCoreAsync(
        EndpointBinding binding,
        ITransportAdapter adapter,
        Recorder recorder,
        ILogger runnerLogger,
        IReadOnlyList<string> waitForIds,
        TimeSpan timeout)
    {
        if (adapter is SimpleAdapter sa) sa.Recorder = recorder;

        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<RecordingConsumer>();
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
            runnerLogger);

        using var cts = new CancellationTokenSource(timeout);
        Task runTask = runner.RunAsync(cts.Token);

        foreach (string id in waitForIds)
        {
            await recorder.WaitForAsync(id, cts.Token);
        }

        await Task.Delay(150, CancellationToken.None);
        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    // ── Simple IConsumerOrderingConfiguration ─────────────────────────────────────────────────────

    private sealed class TestOrdering : IConsumerOrderingConfiguration
    {
        public string? HeaderName => "ordering-key";
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency => null;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public TransportAffinity TransportAffinity => TransportAffinity.None;
        public int MaxDeliveryAttempts => 0;
    }

    // ── Consumer ──────────────────────────────────────────────────────────────────────────────────

    // Header name used to carry the original test message ID (string) into the consumer,
    // since InboundMessage.MessageId is parsed as Guid (non-GUID strings → Guid.Empty).
    private const string TestMsgIdHeader = "test-msg-id";

    private sealed class RecordingConsumer(Recorder recorder) : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context)
        {
            // Read the original message ID from the test header (not context.MessageId,
            // which is a Guid parsed from the string — non-GUID test IDs become Guid.Empty).
            string id = context.Headers.TryGetValue(TestMsgIdHeader, out string? hdr)
                ? hdr
                : context.MessageId.ToString();
            recorder.Record(id);
            return Task.CompletedTask;
        }
    }

    // ── Recorder ──────────────────────────────────────────────────────────────────────────────────

    private sealed class Recorder
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _tcs = new();

        internal void Record(string id)
        {
            _tcs.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
        }

        internal async Task WaitForAsync(string id, CancellationToken ct)
        {
            TaskCompletionSource tcs = _tcs.GetOrAdd(
                id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // ── Fake adapter ──────────────────────────────────────────────────────────────────────────────

    private sealed class SimpleAdapter(
        IReadOnlyList<(string MessageId, string Key, string? epochHeader)> messages)
        : ITransportAdapter
    {
        internal Recorder? Recorder { get; set; }

        public string TransportName => "SimpleEpochFake";
        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                var (msgId, key, epochHeader) = messages[i];
                var headers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ordering-key"] = key,
                    [TestMsgIdHeader] = msgId,
                };
                if (epochHeader is not null)
                {
                    headers[MappingEpochTracker.MappingEpochHeaderName] = epochHeader;
                }
                yield return new InboundMessage(
                    messageId: msgId,
                    headers: headers,
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
            => Task.CompletedTask;

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(
            BareWire.Abstractions.Topology.TopologyDeclaration topology,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    // ── Minimal capturing logger ──────────────────────────────────────────────────────────────────

    private sealed class FakeLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        internal IReadOnlyList<string> Warnings => _warnings;
        internal int WarningCount => _warnings.Count;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
