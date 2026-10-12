using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Pins what a stopping in-memory consume call does with a delivery that a consumer (for example a
/// per-key ordering lane) is still processing: it keeps the delivery claimable for a bounded time, so a
/// settlement that arrives after the stop reaches the queue instead of becoming a no-op followed by a
/// requeued duplicate. Ordering is driven by a signal raised when the wait starts, never by elapsed time.
/// </summary>
public sealed class InMemoryTransportAdapterShutdownSettleTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Consume_StopWithUnsettledDelivery_AckDuringWaitRemovesMessage()
    {
        // Arrange — default budget and system time; one message taken and not yet settled.
        var logger = new SignalingLogger();
        using InMemoryTransportAdapter adapter = CreateAdapter(logger);
        Enqueue(GetQueue(adapter), "m-1");
        using var cts = new CancellationTokenSource();
        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync("orders", new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage message = enumerator.Current;

        // Act — stop, then settle the message the way a lane that finishes after the stop does.
        await cts.CancelAsync();
        Task end = enumerator.MoveNextAsync().AsTask();
        Task endObserved = end.ContinueWith(static _ => { }, TaskScheduler.Default);
        await Task.WhenAny(end, logger.WaitStarted.Task).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.SettleAsync(SettlementAction.Ack, message, TestContext.Current.CancellationToken);
        await endObserved.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        GetQueue(adapter).Occupancy.Should().Be(0, "a late lane Ack must not leave a requeued duplicate");
        adapter.InFlight.Count.Should().Be(0);
    }

    [Fact]
    public async Task Consume_StopWithDeliveryNeverSettled_RequeuesAfterBudget()
    {
        // Arrange — fake time; one message taken and never settled.
        var fake = new FakeTimeProvider();
        var logger = new SignalingLogger();
        using InMemoryTransportAdapter adapter = CreateAdapter(logger, fake);
        Enqueue(GetQueue(adapter), "m-1");
        using var cts = new CancellationTokenSource();
        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync("orders", new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken))
            .Should().BeTrue();

        // Act — stop; once the wait has started, move fake time past the budget.
        await cts.CancelAsync();
        Task end = enumerator.MoveNextAsync().AsTask();
        Task endObserved = end.ContinueWith(static _ => { }, TaskScheduler.Default);
        await logger.WaitStarted.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await FakeTimePump.AdvanceUntilCompletedAsync(
            fake, endObserved, InMemorySettlementDrain.Budget, Guard);

        // Assert — released exactly as before the wait existed: the delivery is back on the queue.
        GetQueue(adapter).Occupancy.Should().Be(1, "after the budget the delivery is requeued");
        logger.Inner.Entries.Should().ContainSingle(e => e.EventId.Name == "LogSettlementBudgetElapsed");
    }

    [Fact]
    public async Task Consume_StopWithNothingInFlight_ReleasesWithoutWaiting()
    {
        // Arrange — fake time that is never advanced; the only message is settled before the stop.
        var fake = new FakeTimeProvider();
        var logger = new SignalingLogger();
        using InMemoryTransportAdapter adapter = CreateAdapter(logger, fake);
        Enqueue(GetQueue(adapter), "m-1");
        using var cts = new CancellationTokenSource();
        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync("orders", new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        await adapter.SettleAsync(SettlementAction.Ack, enumerator.Current, TestContext.Current.CancellationToken);

        // Act — the enumerator must end without any time passing.
        await cts.CancelAsync();
        Task end = enumerator.MoveNextAsync().AsTask();
        Task endObserved = end.ContinueWith(static _ => { }, TaskScheduler.Default);
        await endObserved.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        logger.Inner.Entries.Should().NotContain(e => e.EventId.Name == "LogSettlementDrainWaitStarted");
        GetQueue(adapter).Occupancy.Should().Be(0);
    }

    [Fact] // guard — fails if the wait were not aborted by adapter disposal
    public async Task DisposeAsync_DuringSettlementWait_AbortsWaitWithoutBudget()
    {
        // Arrange — fake time that is never advanced; one message taken and never settled.
        var fake = new FakeTimeProvider();
        var logger = new SignalingLogger();
        InMemoryTransportAdapter adapter = CreateAdapter(logger, fake);
        Enqueue(GetQueue(adapter), "m-1");
        using var cts = new CancellationTokenSource();
        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync("orders", new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken))
            .Should().BeTrue();

        // Act — stop, wait until the runner is waiting, then dispose the adapter.
        await cts.CancelAsync();
        Task end = enumerator.MoveNextAsync().AsTask();
        Task endObserved = end.ContinueWith(static _ => { }, TaskScheduler.Default);
        await Task.WhenAny(end, logger.WaitStarted.Task).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
        await endObserved.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert — ended with no fake time passing and no budget warning.
        logger.Inner.Entries.Should().NotContain(e => e.EventId.Name == "LogSettlementBudgetElapsed");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────────

    private static InMemoryTransportAdapter CreateAdapter(SignalingLogger logger, TimeProvider? timeProvider = null)
    {
        var configurator = new InMemoryConfigurator();
        configurator.ConfigureTopology(t => t.DeclareQueue("orders"));
        InMemoryTransportOptions options = configurator.Build();
        return new InMemoryTransportAdapter(options, new InMemoryBroker(options), logger, timeProvider: timeProvider);
    }

    private static InMemoryQueue GetQueue(InMemoryTransportAdapter adapter)
    {
        adapter.Broker.TryGetQueue("orders", out InMemoryQueue? queue).Should().BeTrue();
        return queue!;
    }

    private static void Enqueue(InMemoryQueue queue, string id)
    {
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
        int length = Encoding.UTF8.GetBytes(id, buffer);
        InMemoryHeaderSet headers = InMemoryHeaderSet.Stamp(
            new Dictionary<string, string> { [InMemoryHeaderNames.MessageId] = id },
            string.Empty,
            queue.Name,
            string.Empty);
        queue.WriteReserved(new InMemoryDelivery(buffer, length, headers));
    }

    /// <summary>Captures entries and completes <see cref="WaitStarted"/> when a stopping consumer starts waiting.</summary>
    private sealed class SignalingLogger : ILogger<InMemoryTransportAdapter>
    {
        internal CapturingLogger<InMemoryTransportAdapter> Inner { get; } = new();

        internal TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Inner.Log(logLevel, eventId, state, exception, formatter);
            if (eventId.Name == "LogSettlementDrainWaitStarted")
            {
                WaitStarted.TrySetResult();
            }
        }
    }
}
