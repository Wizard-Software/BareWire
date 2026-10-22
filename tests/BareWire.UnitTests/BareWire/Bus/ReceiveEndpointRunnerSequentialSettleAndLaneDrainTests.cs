using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.UnitTests.Transport;
using BareWire.UnitTests.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Pins two shutdown defects of <c>ReceiveEndpointRunner</c> and their fixes. (1) The sequential path
/// (per-key ordering off) must settle with a token that outlives the stop by a fixed budget, must not read
/// another message once the stop was requested, and must log a failed settlement by exception type only.
/// (2) An ordered lane whose worker was abandoned (a fault, or a cancelled park) must hand every item it
/// still holds back to the broker, release the credit and return the pooled buffer — throttled while the
/// endpoint keeps running, immediately and bounded by the settlement budget once it is stopping. The runner
/// is driven against a scripted in-process adapter that records every settlement.
/// </summary>
public sealed class ReceiveEndpointRunnerSequentialSettleAndLaneDrainTests
{
    private const string QueueName = "b38-runner-endpoint";

    // Mirrors the settlement budget of the runner: a settlement token is cancelled this long after the
    // runner's own token was cancelled.
    private static readonly TimeSpan SettlementBudget = TimeSpan.FromSeconds(5);

    // The documented pause between two requeues of an abandoned lane while the endpoint keeps running.
    private static readonly TimeSpan DrainThrottle = TimeSpan.FromMilliseconds(100);

    // Granularity of the fake clock while a test waits for throttled work.
    private static readonly TimeSpan PumpStep = TimeSpan.FromMilliseconds(10);

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private const string SecretDetail = "broker-secret-detail-4711";

    // ── Defect 1: sequential path settlement ─────────────────────────────────────────────────────

    [Fact] // B38 (1) — RED before the fix: the Ack goes out with the already cancelled runner token
    public async Task RunAsync_SequentialStopDuringHandler_AcksWithUncancelledSettlementToken()
    {
        // Arrange — sequential endpoint; the consumer ignores its token and blocks until released. The adapter
        // behaves like a broker SDK: a settlement with a cancelled token throws OperationCanceledException.
        var adapter = new SettleRecordingAdapter(messageCount: 1);
        var gate = new GateState();
        ReceiveEndpointRunner runner = CreateRunner(
            adapter, gate, NullLogger<ReceiveEndpointRunner>.Instance, concurrency: 1, ordered: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act — stop while the handler runs, then let the handler succeed.
        Task runTask = runner.RunAsync(cts.Token);
        await gate.Entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        gate.Release.SetResult();
        await runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        SettleRecord settlement = adapter.Settlements.Should().ContainSingle().Which;
        settlement.MessageId.Should().Be("m-0");
        settlement.Action.Should().Be(SettlementAction.Ack);
        settlement.TokenCancelledAtCall.Should().BeFalse(
            "the settlement must use a token that outlives the stop by the settlement budget");
    }

    [Fact] // B38 (1) — RED before the fix: no budget token, so no discarded-settlement entry
    public async Task RunAsync_SequentialSettleOutlivesBudget_LogsDebugAndEndsCleanly()
    {
        // Arrange — the Ack waits on its token; time is fake so the test drives the budget.
        var adapter = new SettleRecordingAdapter(messageCount: 1) { BlockWhen = action => action == SettlementAction.Ack };
        var gate = new GateState();
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        var flow = new FlowController(NullLogger<FlowController>.Instance);
        var fake = new FakeTimeProvider();
        ReceiveEndpointRunner runner = CreateRunner(
            adapter, gate, logger, concurrency: 1, ordered: false, timeProvider: fake, flowController: flow);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act — stop during the handler, release it, and let the budget elapse while the Ack is pending.
        Task runTask = runner.RunAsync(cts.Token);
        await gate.Entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        gate.Release.SetResult();
        await adapter.SettleEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        runTask.IsCompleted.Should().BeFalse("the Ack is still pending inside the budget");
        await FakeTimePump.AdvanceUntilCompletedAsync(fake, runTask, SettlementBudget, Guard);

        // Assert
        adapter.Settlements.Should().ContainSingle().Which.TokenCancelledAtCall.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.EventId.Name == "LogSequentialSettlementDiscarded")
            .Which.Level.Should().Be(LogLevel.Debug);
        logger.Entries.Should().NotContain(e => e.EventId.Name == "LogConsumeLoopFaulted");
        InFlight(flow).Should().Be(0);
    }

    [Theory] // B38 Q2-A — RED before the fix for Ack / Nack / Reject: the loop reads on after settling
    [InlineData(SettlementAction.Ack, ConsumerKind.Gated)]
    [InlineData(SettlementAction.Nack, ConsumerKind.Throwing)]
    [InlineData(SettlementAction.Reject, ConsumerKind.None)]
    [InlineData(SettlementAction.Requeue, ConsumerKind.CancelsRunnerThenFails)]
    public async Task RunAsync_SequentialSettleAfterStopRequested_DoesNotReceiveAnotherMessage(
        SettlementAction expected, ConsumerKind consumer)
    {
        // Arrange — three ready messages; the first settlement requests the stop. The adapter accepts a
        // settlement with a cancelled token, so only the loop itself can stop the next read.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var adapter = new SettleRecordingAdapter(messageCount: 3)
        {
            ThrowOnCancelledSettle = false,
            OnSettle = _ => cts.Cancel(),
        };
        var gate = new GateState { OnInvoke = () => cts.Cancel() };
        gate.Release.SetResult();
        ReceiveEndpointRunner runner = CreateRunner(
            adapter, gate, NullLogger<ReceiveEndpointRunner>.Instance, concurrency: 1, ordered: false,
            consumer: consumer);

        // Act
        await runner.RunAsync(cts.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        adapter.Settlements.Should().ContainSingle(
            "the loop ends after the settlement that was in progress when the stop was requested")
            .Which.Action.Should().Be(expected);
        adapter.PulledAfterStop.Should().Be(0, "a stop that is already requested must not ask the adapter for more");
    }

    [Fact] // P-B38-3 — GREEN before and after: a requeue keeps its non-cancellable token
    public async Task RunAsync_SequentialRequeueAfterStop_StillUsesNonCancellableToken()
    {
        // Arrange — the handler requests the stop and fails with a cancellation, which settles as Requeue.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var adapter = new SettleRecordingAdapter(messageCount: 2) { ThrowOnCancelledSettle = false };
        var gate = new GateState { OnInvoke = () => cts.Cancel() };
        ReceiveEndpointRunner runner = CreateRunner(
            adapter, gate, NullLogger<ReceiveEndpointRunner>.Instance, concurrency: 1, ordered: false,
            consumer: ConsumerKind.CancelsRunnerThenFails);

        // Act
        await runner.RunAsync(cts.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        SettleRecord settlement = adapter.Settlements.Should().ContainSingle().Which;
        settlement.Action.Should().Be(SettlementAction.Requeue);
        settlement.TokenCanBeCancelled.Should().BeFalse("a requeue must always reach the broker");
        adapter.PulledAfterStop.Should().Be(0);
    }

    [Fact] // B38 Q4-A — RED before the fix: the full exception object is logged
    public async Task RunAsync_SequentialSettleFails_LogsExceptionTypeOnly()
    {
        // Arrange — the broker rejects the Ack with an exception whose message carries broker detail.
        var adapter = new SettleRecordingAdapter(messageCount: 1)
        {
            FailWith = action => action == SettlementAction.Ack ? new InvalidOperationException(SecretDetail) : null,
        };
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        var gate = new GateState();
        gate.Release.SetResult();
        ReceiveEndpointRunner runner = CreateRunner(adapter, gate, logger, concurrency: 1, ordered: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task runTask = runner.RunAsync(cts.Token);
        await adapter.SettleEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        LogEntry entry = logger.Entries
            .Should().ContainSingle(e => e.Message.Contains("settl", StringComparison.OrdinalIgnoreCase)).Which;
        entry.Exception.Should().BeNull("the exception object (and its message) must not be logged");
        entry.State.Should().Contain(p =>
            p.Key == "ExceptionType" && (string?)p.Value == nameof(InvalidOperationException));
        logger.Entries.Should().NotContain(e =>
            e.Message.Contains(SecretDetail, StringComparison.Ordinal)
            || (e.Exception != null && e.Exception.Message.Contains(SecretDetail, StringComparison.Ordinal)));
    }

    // ── Defect 2: abandoned ordered lane ─────────────────────────────────────────────────────────

    [Fact] // B38 (2) — RED before the fix: queued items are never requeued, disposed or released
    public async Task RunAsync_LaneFaultsWithQueuedItems_RequeuesAndReleasesEveryQueuedItem()
    {
        // Arrange — one lane, one allowed attempt: m-0 is parked, m-1 and m-2 are queued behind it.
        var adapter = new SettleRecordingAdapter(messageCount: 3);
        using LaneFaultRun run = await StartStoppedLaneFaultRunAsync(adapter);

        // Act
        await run.RunTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        adapter.Settlements.Where(s => s.Action == SettlementAction.Requeue).Select(s => s.MessageId)
            .Should().Equal("m-1", "m-2");
        adapter.Settlements.Should().HaveCount(2, "the faulted head m-0 is not settled, only the queued items are");
        adapter.DisposedMessageIds.Should().Equal("m-0", "m-1", "m-2");
        InFlight(run.Flow).Should().Be(0);
        run.Logger.Entries.Count(e => e.EventId.Name == "LogLaneWorkerFaulted").Should().Be(1);
    }

    [Theory] // B38 (2) — RED before the fix: the reader blocks on the full dead lane
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_LaneFaultedAndFull_ReaderKeepsPullingAndEveryItemIsRequeued(bool parkThrowsImmediately)
    {
        // Arrange — prefetch 2 over 2 lanes gives a lane depth of 1; every message has the same ordering key,
        // so they all go to one lane. Four messages: more than the lane and the credit can hold.
        var adapter = new SettleRecordingAdapter(messageCount: 4);
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        var flow = new FlowController(NullLogger<FlowController>.Instance);
        var fake = new FakeTimeProvider();
        if (parkThrowsImmediately)
        {
            adapter.ParkRelease.SetResult();
        }

        ReceiveEndpointRunner runner = CreateParkingRunner(
            adapter, logger, concurrency: 2, prefetch: 2, flowController: flow, timeProvider: fake);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act — m-0 is parked; once the reader holds m-2 (the lane and the credit are full) the park fails.
        Task runTask = runner.RunAsync(cts.Token);
        await adapter.ParkEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        if (!parkThrowsImmediately)
        {
            await adapter.Yielded.WaitForAsync(3).WaitAsync(Guard, TestContext.Current.CancellationToken);
            adapter.ParkRelease.SetResult();
        }

        // The reader must get past the dead lane and pull the last message; drained requeues are throttled
        // on the runner clock, so fake time is pumped until each condition holds.
        await FakeTimePump.AdvanceUntilCompletedAsync(fake, adapter.AllHandedOff.Task, PumpStep, Guard);
        await FakeTimePump.AdvanceUntilCompletedAsync(fake, adapter.Requeued.WaitForAsync(3), PumpStep, Guard);
        await cts.CancelAsync();
        await runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        adapter.Settlements.Where(s => s.Action == SettlementAction.Requeue).Select(s => s.MessageId)
            .Should().BeEquivalentTo(["m-1", "m-2", "m-3"], "each queued item is requeued exactly once");
        adapter.Settlements.Should().HaveCount(3);
        adapter.DisposedMessageIds.Should().Equal("m-0", "m-1", "m-2", "m-3");
        InFlight(flow).Should().Be(0);
    }

    [Fact] // characterization — GREEN before and after: a healthy lane still processes its queue on stop
    public async Task RunAsync_HealthyLaneOnStop_ProcessesQueuedItemsWithoutAbandonedLaneDrain()
    {
        // Arrange — one lane, three messages; m-0 is in the handler, m-1 and m-2 are queued.
        var adapter = new SettleRecordingAdapter(messageCount: 3);
        var gate = new GateState();
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        var flow = new FlowController(NullLogger<FlowController>.Instance);
        ReceiveEndpointRunner runner = CreateRunner(adapter, gate, logger, concurrency: 1, flowController: flow);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act — stop while m-0 is in the handler, then release the handler.
        Task runTask = runner.RunAsync(cts.Token);
        await gate.Entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.AllHandedOff.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        gate.Release.SetResult();
        await runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert — every item went through normal processing and settlement.
        adapter.Settlements.Select(s => s.MessageId).Should().BeEquivalentTo(["m-0", "m-1", "m-2"]);
        adapter.Settlements.Should().OnlyContain(s => !s.TokenCancelledAtCall);
        adapter.DisposedMessageIds.Should().Equal("m-0", "m-1", "m-2");
        InFlight(flow).Should().Be(0);
        logger.Entries.Should().NotContain(e =>
            e.EventId.Name == "LogLaneWorkerFaulted"
            || (e.EventId.Name != null && e.EventId.Name.StartsWith("LogAbandonedLane", StringComparison.Ordinal)));
    }

    [Fact] // B38 K-6 — RED before the fix: a park cancelled by the stop leaves the queued items stranded
    public async Task RunAsync_LaneAbandonedByCancelledPark_DrainsQueuedItemsAndIsNotAFault()
    {
        // Arrange — the park attempt waits until the stop cancels it; m-1 and m-2 are queued behind m-0.
        var adapter = new SettleRecordingAdapter(messageCount: 3) { ParkMode = ParkMode.WaitForCancellation };
        using LaneFaultRun run = await StartStoppedLaneFaultRunAsync(adapter);

        // Act
        await run.RunTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        adapter.Settlements.Where(s => s.Action == SettlementAction.Requeue).Select(s => s.MessageId)
            .Should().Equal("m-1", "m-2");
        adapter.DisposedMessageIds.Should().Equal("m-0", "m-1", "m-2");
        InFlight(run.Flow).Should().Be(0);
        run.Logger.Entries.Should().NotContain(e =>
            e.EventId.Name == "LogLaneWorkerFaulted" || e.EventId.Name == "LogConsumeLoopFaulted");
    }

    [Fact] // B38 K-6 — RED before the fix: nothing is requeued, so nothing can fail and be logged
    public async Task RunAsync_AbandonedLaneRequeueThrows_LogsPerItemAndStillDisposesAndReleasesCredit()
    {
        // Arrange — every requeue fails with an exception whose message carries broker detail.
        var adapter = new SettleRecordingAdapter(messageCount: 3)
        {
            FailWith = action => action == SettlementAction.Requeue ? new InvalidOperationException(SecretDetail) : null,
        };
        using LaneFaultRun run = await StartStoppedLaneFaultRunAsync(adapter);

        // Act
        await run.RunTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert — the drain goes on after the first failure and every item is still disposed and released.
        adapter.Settlements.Where(s => s.Action == SettlementAction.Requeue).Select(s => s.MessageId)
            .Should().Equal("m-1", "m-2");
        adapter.DisposedMessageIds.Should().Equal("m-0", "m-1", "m-2");
        InFlight(run.Flow).Should().Be(0);
        run.Logger.Entries.Count(e => e.EventId.Name == "LogAbandonedLaneRequeueFailed").Should().Be(2);
        run.Logger.Entries.Should().NotContain(e =>
            e.Message.Contains(SecretDetail, StringComparison.Ordinal)
            || (e.Exception != null && e.Exception.Message.Contains(SecretDetail, StringComparison.Ordinal)));
    }

    [Fact] // B38 Q1-A — RED before the fix: nothing is requeued, so the pump never sees a requeue
    public async Task RunAsync_AbandonedLaneWhileRunning_ThrottlesRequeuesOnRunnerClock()
    {
        // Arrange — four messages on one lane; the park fails at once, so m-1..m-3 are drained during
        // normal running (no stop). Fake time is held until the test pumps it.
        var fake = new FakeTimeProvider();
        var adapter = new SettleRecordingAdapter(messageCount: 4, clock: fake);
        adapter.ParkRelease.SetResult();
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        ReceiveEndpointRunner runner = CreateParkingRunner(adapter, logger, concurrency: 1, timeProvider: fake);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        DateTimeOffset start = fake.GetUtcNow();

        // Act
        Task runTask = runner.RunAsync(cts.Token);
        await adapter.ParkEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await FakeTimePump.AdvanceUntilCompletedAsync(fake, adapter.Requeued.WaitForAsync(3), PumpStep, Guard);
        await cts.CancelAsync();
        await runTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert — each requeue waited at least the throttle on the runner clock after the previous event.
        IReadOnlyList<DateTimeOffset> times = adapter.RequeueTimes;
        times.Should().HaveCount(3);
        (times[0] - start).Should().BeGreaterThanOrEqualTo(DrainThrottle, "the first requeue is throttled too");
        (times[1] - times[0]).Should().BeGreaterThanOrEqualTo(DrainThrottle);
        (times[2] - times[1]).Should().BeGreaterThanOrEqualTo(DrainThrottle);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.EventId.Name != null
            && e.EventId.Name.StartsWith("LogAbandonedLane", StringComparison.Ordinal),
            "an abandoned lane is reported at Warning level");
    }

    [Fact] // B38 Q1-A — RED before the fix: nothing is requeued; once stopping there must be no delay at all
    public async Task RunAsync_AbandonedLaneAfterStop_RequeuesWithoutAnyDelay()
    {
        // Arrange — fake time is never advanced, so any delay before a requeue would hang the run.
        var adapter = new SettleRecordingAdapter(messageCount: 3);
        var fake = new FakeTimeProvider();
        using LaneFaultRun run = await StartStoppedLaneFaultRunAsync(adapter, fake);
        DateTimeOffset stopTime = fake.GetUtcNow();

        // Act
        await run.RunTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        adapter.Settlements.Where(s => s.Action == SettlementAction.Requeue).Select(s => s.MessageId)
            .Should().Equal("m-1", "m-2");
        fake.GetUtcNow().Should().Be(stopTime, "the drain after a stop does not wait on the runner clock");
        adapter.DisposedMessageIds.Should().Equal("m-0", "m-1", "m-2");
        InFlight(run.Flow).Should().Be(0);
    }

    [Fact] // B38 Q3-A — RED before the fix: nothing is requeued; the drain must use the budget token
    public async Task RunAsync_AbandonedLaneDrainAfterStop_RequeuesWithLiveBudgetToken()
    {
        // Arrange
        var adapter = new SettleRecordingAdapter(messageCount: 3);
        using LaneFaultRun run = await StartStoppedLaneFaultRunAsync(adapter);

        // Act
        await run.RunTask.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert — a cancellable token (the budget), not CancellationToken.None, and not yet cancelled.
        List<SettleRecord> requeues = adapter.Settlements.Where(s => s.Action == SettlementAction.Requeue).ToList();
        requeues.Should().HaveCount(2);
        requeues.Should().OnlyContain(s => s.TokenCanBeCancelled && !s.TokenCancelledAtCall);
    }

    [Fact] // B38 Q3-A — RED before the fix: the items are neither requeued nor disposed
    public async Task RunAsync_AbandonedLaneDrainOutlivesBudget_LogsDebugAndStillDisposesEveryItem()
    {
        // Arrange — the broker never answers a requeue; time is fake so the test drives the budget.
        var adapter = new SettleRecordingAdapter(messageCount: 3) { BlockWhen = action => action == SettlementAction.Requeue };
        var fake = new FakeTimeProvider();
        using LaneFaultRun run = await StartStoppedLaneFaultRunAsync(adapter, fake);

        // Act — the budget elapses while the first requeue is pending.
        await FakeTimePump.AdvanceUntilCompletedAsync(fake, run.RunTask, SettlementBudget, Guard);

        // Assert — the run ends without an exception, nothing leaks, the discarded wait is logged at Debug.
        adapter.DisposedMessageIds.Should().Equal("m-0", "m-1", "m-2");
        InFlight(run.Flow).Should().Be(0);
        run.Logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Debug && e.EventId.Name != null
            && e.EventId.Name.Contains("AbandonedLane", StringComparison.Ordinal));
        run.Logger.Entries.Should().NotContain(e => e.EventId.Name == "LogConsumeLoopFaulted");
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────

    private static ReceiveEndpointRunner CreateRunner(
        ITransportAdapter adapter,
        GateState gate,
        ILogger logger,
        int concurrency,
        int maxDeliveryAttempts = 0,
        TimeProvider? timeProvider = null,
        bool ordered = true,
        ConsumerKind consumer = ConsumerKind.Gated,
        string? deadLetterExchange = null,
        int prefetch = 32,
        FlowController? flowController = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddScoped<GatedRawConsumer>();
        services.AddScoped<ThrowingRawConsumer>();
        services.AddScoped<CancelThenFailRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());

        Type[] rawConsumers = consumer switch
        {
            ConsumerKind.Gated => [typeof(GatedRawConsumer)],
            ConsumerKind.Throwing => [typeof(ThrowingRawConsumer)],
            ConsumerKind.CancelsRunnerThenFails => [typeof(CancelThenFailRawConsumer)],
            _ => [],
        };

        var binding = new EndpointBinding
        {
            EndpointName = QueueName,
            PrefetchCount = prefetch,
            ConcurrentMessageLimit = concurrency,
            Ordering = ordered ? new SettleTestOrdering(concurrency, maxDeliveryAttempts) : null,
            RawConsumers = rawConsumers,
            DeadLetterExchange = deadLetterExchange,
            RetryCount = 1,
        };

        return new ReceiveEndpointRunner(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            flowController ?? new FlowController(NullLogger<FlowController>.Instance),
            new NullInstrumentation(),
            logger,
            timeProvider: timeProvider);
    }

    /// <summary>One ordered lane, one allowed delivery attempt, a thrown handler and a configured dead-letter target.</summary>
    private static ReceiveEndpointRunner CreateParkingRunner(
        ITransportAdapter adapter,
        ILogger logger,
        int concurrency = 1,
        int prefetch = 32,
        FlowController? flowController = null,
        TimeProvider? timeProvider = null) =>
        CreateRunner(
            adapter,
            new GateState(),
            logger,
            concurrency,
            maxDeliveryAttempts: 1,
            timeProvider: timeProvider,
            consumer: ConsumerKind.Throwing,
            deadLetterExchange: "dlx",
            prefetch: prefetch,
            flowController: flowController);

    /// <summary>
    /// Starts a one-lane run in which m-0 is parked and the remaining messages are queued behind it, then
    /// requests the stop and lets the park fail (or be cancelled). The returned run is still in flight.
    /// </summary>
    private static async Task<LaneFaultRun> StartStoppedLaneFaultRunAsync(
        SettleRecordingAdapter adapter, TimeProvider? timeProvider = null)
    {
        var logger = new CapturingLogger<ReceiveEndpointRunner>();
        var flow = new FlowController(NullLogger<FlowController>.Instance);
        ReceiveEndpointRunner runner = CreateParkingRunner(
            adapter, logger, concurrency: 1, flowController: flow, timeProvider: timeProvider);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task runTask = runner.RunAsync(cts.Token);
        await adapter.ParkEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.AllHandedOff.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        adapter.ParkRelease.TrySetResult();

        return new LaneFaultRun(runTask, logger, flow, cts);
    }

    private static int InFlight(FlowController flowController) =>
        flowController.GetOrCreateManager(QueueName, new FlowControlOptions { MaxInFlightMessages = 1 }).InflightCount;

    private static InboundMessage NewMessage(string messageId, IReadOnlyDictionary<string, string> headers, ulong tag)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
        return new InboundMessage(
            messageId: messageId,
            headers: headers,
            body: new ReadOnlySequence<byte>(buffer, 0, 16),
            deliveryTag: tag,
            pooledBuffer: buffer);
    }

    public enum ConsumerKind
    {
        /// <summary>Blocks on the gate, then succeeds (Ack).</summary>
        Gated,

        /// <summary>Throws a regular exception (Nack).</summary>
        Throwing,

        /// <summary>Requests the stop through <see cref="GateState.OnInvoke"/> and fails with a cancellation (Requeue).</summary>
        CancelsRunnerThenFails,

        /// <summary>No consumer at all, so no handler matches (Reject).</summary>
        None,
    }

    internal enum ParkMode
    {
        /// <summary>Waits for <see cref="SettleRecordingAdapter.ParkRelease"/>, then throws.</summary>
        ThrowAfterRelease,

        /// <summary>Waits until the passed token is cancelled.</summary>
        WaitForCancellation,
    }

    /// <summary>One settlement as seen by <see cref="SettleRecordingAdapter"/>.</summary>
    internal readonly record struct SettleRecord(
        string MessageId, SettlementAction Action, bool TokenCancelledAtCall, bool TokenCanBeCancelled);

    private sealed class LaneFaultRun(
        Task runTask, CapturingLogger<ReceiveEndpointRunner> logger, FlowController flow, CancellationTokenSource cts)
        : IDisposable
    {
        internal Task RunTask { get; } = runTask;

        internal CapturingLogger<ReceiveEndpointRunner> Logger { get; } = logger;

        internal FlowController Flow { get; } = flow;

        public void Dispose() => cts.Dispose();
    }

    private sealed class GateState
    {
        /// <summary>Resolved by the first consumer invocation.</summary>
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed by the test to let the consumer return; the consumer ignores its own token.</summary>
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Invoked at the start of every consumer invocation.</summary>
        internal Action? OnInvoke { get; init; }
    }

    private sealed class GatedRawConsumer(GateState gate) : IRawConsumer
    {
        public async Task ConsumeAsync(RawConsumeContext context)
        {
            gate.OnInvoke?.Invoke();
            gate.Entered.TrySetResult();
            await gate.Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class ThrowingRawConsumer : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context) => throw new InvalidOperationException("consumer failed");
    }

    private sealed class CancelThenFailRawConsumer(GateState gate) : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context)
        {
            gate.OnInvoke?.Invoke();
            throw new OperationCanceledException("consumer cancelled by the stop");
        }
    }

    /// <summary>Minimal read-only ordering carrier with a configurable lane count and attempt limit.</summary>
    private sealed class SettleTestOrdering(int concurrency, int maxDeliveryAttempts) : IConsumerOrderingConfiguration
    {
        public string? HeaderName => "seq";
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency => concurrency;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public TransportAffinity TransportAffinity => TransportAffinity.None;
        public int MaxDeliveryAttempts => maxDeliveryAttempts;
    }

    /// <summary>Awaitable "count reached N" signal.</summary>
    private sealed class CountSignal
    {
        private readonly Lock _lock = new();
        private readonly List<(int Target, TaskCompletionSource Source)> _waiters = [];
        private int _count;

        internal int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        internal void Increment()
        {
            List<TaskCompletionSource>? ready = null;
            lock (_lock)
            {
                _count++;
                for (int i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_count >= _waiters[i].Target)
                    {
                        (ready ??= []).Add(_waiters[i].Source);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            if (ready is not null)
            {
                foreach (TaskCompletionSource source in ready)
                {
                    source.TrySetResult();
                }
            }
        }

        internal Task WaitForAsync(int target)
        {
            lock (_lock)
            {
                if (_count >= target)
                {
                    return Task.CompletedTask;
                }

                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((target, source));
                return source.Task;
            }
        }
    }

    /// <summary>
    /// Yields <c>messageCount</c> ready messages that all share one ordering key (<c>seq</c> = <c>k</c>), signals
    /// once the reader asked for a next message after the last one (so every message was handed to its owner),
    /// then waits for cancellation. Records every settlement together with the state of the token it was given
    /// and — like a broker SDK — throws on a cancelled token unless told otherwise. Implements the durable-park
    /// seam of the ordered path.
    /// </summary>
    private sealed class SettleRecordingAdapter(int messageCount, TimeProvider? clock = null)
        : ITransportAdapter, IDurableParkSettlement
    {
        private static readonly Dictionary<string, string> SharedKey = new(StringComparer.Ordinal) { ["seq"] = "k" };

        private readonly TimeProvider _clock = clock ?? TimeProvider.System;
        private readonly ConcurrentQueue<SettleRecord> _settlements = new();
        private readonly ConcurrentQueue<DateTimeOffset> _requeueTimes = new();
        private readonly ConcurrentDictionary<string, InboundMessage> _issued = new(StringComparer.Ordinal);
        private int _pulledAfterStop;

        internal IReadOnlyCollection<SettleRecord> Settlements => _settlements;

        internal IReadOnlyList<DateTimeOffset> RequeueTimes => [.. _requeueTimes];

        /// <summary>Ids of the issued messages whose pooled buffer was returned (disposed), in issue order.</summary>
        internal IReadOnlyList<string> DisposedMessageIds =>
            [.. _issued.Where(p => p.Value.PooledBuffer is null).Select(p => p.Key).Order(StringComparer.Ordinal)];

        /// <summary>Counts how often the consumer asked for a message while its token was already cancelled.</summary>
        internal int PulledAfterStop => Volatile.Read(ref _pulledAfterStop);

        internal CountSignal Yielded { get; } = new();

        internal CountSignal Requeued { get; } = new();

        internal TaskCompletionSource AllHandedOff { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SettleEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ParkEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ParkRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ParkMode ParkMode { get; init; } = ParkMode.ThrowAfterRelease;

        /// <summary>When true (default) a settlement that receives a cancelled token throws like a broker SDK.</summary>
        internal bool ThrowOnCancelledSettle { get; init; } = true;

        /// <summary>Settlements for which this returns true wait on their token and throw when it is cancelled.</summary>
        internal Func<SettlementAction, bool>? BlockWhen { get; init; }

        /// <summary>Settlements for which this returns an exception fail with it after being recorded.</summary>
        internal Func<SettlementAction, Exception?>? FailWith { get; init; }

        /// <summary>Invoked synchronously for every settlement, right after it was recorded.</summary>
        internal Action<SettleRecord>? OnSettle { get; init; }

        public string TransportName => "SettleRecording";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < messageCount; i++)
            {
                NoteMoveNext(cancellationToken);
                string id = $"m-{i}";
                InboundMessage message = NewMessage(id, SharedKey, (ulong)i);
                _issued[id] = message;
                Yielded.Increment();
                yield return message;
            }

            NoteMoveNext(cancellationToken);
            AllHandedOff.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        public async Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            var record = new SettleRecord(
                message.MessageId, action, cancellationToken.IsCancellationRequested, cancellationToken.CanBeCanceled);
            _settlements.Enqueue(record);
            if (action == SettlementAction.Requeue)
            {
                _requeueTimes.Enqueue(_clock.GetUtcNow());
                Requeued.Increment();
            }

            SettleEntered.TrySetResult();
            OnSettle?.Invoke(record);

            if (ThrowOnCancelledSettle && record.TokenCancelledAtCall)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (BlockWhen?.Invoke(action) == true)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            Exception? failure = FailWith?.Invoke(action);
            if (failure is not null)
            {
                throw failure;
            }
        }

        public async Task<DurableSettlementResult> ParkHeadDurablyAsync(
            InboundMessage message,
            string deadLetterExchange,
            string deadLetterRoutingKey,
            CancellationToken cancellationToken = default)
        {
            ParkEntered.TrySetResult();
            if (ParkMode == ParkMode.ThrowAfterRelease)
            {
                await ParkRelease.Task.ConfigureAwait(false);
                throw new InvalidOperationException("park failed");
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new DurableSettlementResult(false, "unreachable");
        }

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(TopologyDeclaration topology, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private void NoteMoveNext(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _pulledAfterStop);
            }
        }
    }
}
