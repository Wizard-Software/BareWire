using AwesomeAssertions;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryQueueTests
{
    private static InMemoryDelivery Delivery() => new(new byte[4], 4);

    // Registers an active consumer by starting (and holding) an enumerator; the test disposes it.
    private static async Task<IAsyncEnumerator<InMemoryDelivery>> StartConsumerAsync(InMemoryQueue q, CancellationToken ct)
    {
        IAsyncEnumerator<InMemoryDelivery> e = q.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        q.TryReserve().Should().Be(QueueReservationResult.Reserved);
        q.WriteReserved(Delivery());
        (await e.MoveNextAsync()).Should().BeTrue(); // one delivery now in flight (read, not released)
        return e;
    }

    [Fact]
    public void TryReserve_BelowCapacity_ReservesAndIncrementsOccupancy()
    {
        var q = new InMemoryQueue("orders", capacity: 2);

        q.TryReserve().Should().Be(QueueReservationResult.Reserved);
        q.Occupancy.Should().Be(1);
    }

    [Fact]
    public void TryReserve_FullWithoutActiveConsumer_SetsLatchAndReturnsLatched()
    {
        var q = new InMemoryQueue("orders", capacity: 2);
        q.TryReserve();
        q.WriteReserved(Delivery());
        q.TryReserve();
        q.WriteReserved(Delivery());

        q.TryReserve().Should().Be(QueueReservationResult.Latched);
        q.IsLatched.Should().BeTrue();
        q.Occupancy.Should().Be(2);
    }

    [Fact]
    public async Task TryReserve_FullWithActiveConsumer_ReturnsFullWithoutLatch()
    {
        var q = new InMemoryQueue("orders", capacity: 2);
        await using IAsyncEnumerator<InMemoryDelivery> e = await StartConsumerAsync(q, TestContext.Current.CancellationToken);
        q.TryReserve();
        q.WriteReserved(Delivery());

        q.TryReserve().Should().Be(QueueReservationResult.Full);
        q.IsLatched.Should().BeFalse();
    }

    [Fact]
    public async Task Occupancy_CountsDeliveriesUntilReleased_IncludingRequeueOnSameSlot()
    {
        // At the queue level a "deferred" or "requeued" delivery is simply read from the channel and
        // rewritten to the same reserved slot: no new reservation is taken and the counter never drops
        // until ReleaseSlot is called.
        var q = new InMemoryQueue("orders", capacity: 2);
        await using IAsyncEnumerator<InMemoryDelivery> e = await StartConsumerAsync(q, TestContext.Current.CancellationToken);
        q.Occupancy.Should().Be(1); // in flight: read from the channel, not settled
        q.TryReserve();
        q.WriteReserved(Delivery());
        q.TryReserve().Should().Be(QueueReservationResult.Full); // in-flight delivery still holds its slot
        InMemoryDelivery inFlight = e.Current;
        q.WriteReserved(inFlight); // requeue on the same slot: no new reservation, never fails
        q.Occupancy.Should().Be(2);
        (await e.MoveNextAsync()).Should().BeTrue(); // read again: still held outside settlement, slot still counted
        q.Occupancy.Should().Be(2);
        q.ReleaseSlot();
        q.TryReserve().Should().Be(QueueReservationResult.Reserved);
    }

    [Fact]
    public void ReleaseSlot_LatchClearsOnlyStrictlyBelowHalfCapacity()
    {
        var q = new InMemoryQueue("orders", capacity: 10);
        for (int i = 0; i < 10; i++)
        {
            q.TryReserve();
            q.WriteReserved(Delivery());
        }

        q.TryReserve().Should().Be(QueueReservationResult.Latched);
        for (int i = 0; i < 5; i++)
        {
            q.ReleaseSlot().Should().BeFalse(); // occupancy 5 == 50% -> still latched
        }

        q.IsLatched.Should().BeTrue();
        q.TryReserve().Should().Be(QueueReservationResult.Latched); // rejected immediately although space exists
        q.Occupancy.Should().Be(5);
        q.ReleaseSlot().Should().BeTrue(); // occupancy 4 < 50% -> latch released
        q.IsLatched.Should().BeFalse();
        q.TryReserve().Should().Be(QueueReservationResult.Reserved);
    }

    [Fact]
    public void TryLatch_WhenOccupancyAlreadyBelowThreshold_DoesNotLeaveLatchSet()
    {
        var q = new InMemoryQueue("orders", capacity: 10);
        q.TryReserve();
        q.WriteReserved(Delivery());

        q.TryLatch().Should().BeFalse();
        q.IsLatched.Should().BeFalse();
    }

    [Fact]
    public async Task TryLatch_FullQueueWithConsumer_LatchesUntilBelowHalf()
    {
        var q = new InMemoryQueue("orders", capacity: 2);
        await using IAsyncEnumerator<InMemoryDelivery> e = await StartConsumerAsync(q, TestContext.Current.CancellationToken);
        q.TryReserve();
        q.WriteReserved(Delivery());

        q.TryLatch().Should().BeTrue();
        q.TryReserve().Should().Be(QueueReservationResult.Latched);
        q.ReleaseSlot().Should().BeFalse(); // occupancy 1 == 50%
        q.ReleaseSlot().Should().BeTrue(); // occupancy 0
    }

    [Fact]
    public void WriteReserved_WithoutReservationBeyondCapacity_Throws()
    {
        var q = new InMemoryQueue("orders", capacity: 1);
        q.TryReserve();
        q.WriteReserved(Delivery());

        Action act = () => q.WriteReserved(Delivery());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ReleaseSlot_WhenEmpty_ThrowsAndKeepsOccupancyAtZero()
    {
        var q = new InMemoryQueue("orders", capacity: 1);

        Action act = () => q.ReleaseSlot();

        act.Should().Throw<InvalidOperationException>();
        q.Occupancy.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveCapacity_Throws(int capacity)
    {
        Action act = () => _ = new InMemoryQueue("orders", capacity);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task WaitToReserveAsync_SpaceAlreadyAvailable_ReturnsReservedWithoutWaiterObject()
    {
        var q = new InMemoryQueue("orders", capacity: 1);

        (await q.WaitToReserveAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken))
            .Should().Be(QueueWaitResult.Reserved);
        q.Occupancy.Should().Be(1);
        q.HasPendingSpaceWaiter.Should().BeFalse();
    }

    [Fact]
    public void TryReserveAndReleaseSlot_FastPath_NeverCreatesWaiterObject()
    {
        var q = new InMemoryQueue("orders", capacity: 2);
        q.TryReserve();
        q.WriteReserved(Delivery());

        q.ReleaseSlot();

        q.HasPendingSpaceWaiter.Should().BeFalse();
    }

    [Fact]
    public async Task WaitToReserveAsync_SlotReleased_ReturnsReservedAndOccupancyRestored()
    {
        var time = new FakeTimeProvider();
        var q = new InMemoryQueue("orders", capacity: 1, time);
        await using IAsyncEnumerator<InMemoryDelivery> consumer = await StartConsumerAsync(q, TestContext.Current.CancellationToken);
        int occupancyBeforeWait = q.Occupancy;

        ValueTask<QueueWaitResult> wait = q.WaitToReserveAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        q.HasPendingSpaceWaiter.Should().BeTrue();
        q.ReleaseSlot();

        (await wait).Should().Be(QueueWaitResult.Reserved);
        q.HasPendingSpaceWaiter.Should().BeFalse();
        q.Occupancy.Should().Be(occupancyBeforeWait); // released then immediately re-reserved: net unchanged
    }

    [Fact]
    public async Task WaitToReserveAsync_NoReleaseWithinTimeout_ReturnsTimedOutAndDoesNotLatch()
    {
        var time = new FakeTimeProvider();
        var q = new InMemoryQueue("orders", capacity: 1, time);
        await using IAsyncEnumerator<InMemoryDelivery> consumer = await StartConsumerAsync(q, TestContext.Current.CancellationToken);

        ValueTask<QueueWaitResult> wait = q.WaitToReserveAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(100));

        (await wait).Should().Be(QueueWaitResult.TimedOut);
        q.IsLatched.Should().BeFalse(); // the caller latches after its own single retry fails
    }

    [Fact]
    public async Task WaitToReserveAsync_AfterTimedOut_WaiterNoLongerPending()
    {
        // Per-waiter FIFO hand-off: a timed-out waiter marks itself abandoned as part of the timeout
        // itself, so it stops counting as a pending waiter immediately — there is no shared wait object
        // to leave installed for anyone else.
        var time = new FakeTimeProvider();
        var q = new InMemoryQueue("orders", capacity: 1, time);
        await using IAsyncEnumerator<InMemoryDelivery> consumer = await StartConsumerAsync(q, TestContext.Current.CancellationToken);

        ValueTask<QueueWaitResult> wait = q.WaitToReserveAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(100));
        (await wait).Should().Be(QueueWaitResult.TimedOut);

        q.HasPendingSpaceWaiter.Should().BeFalse();
    }

    [Fact]
    public async Task WaitToReserveAsync_TimedOutWaiter_DoesNotConsumeLaterRelease()
    {
        // A release that arrives after the waiter has already abandoned its wait must fall back to the
        // normal occupancy decrement instead of handing the slot to a waiter nobody is awaiting anymore.
        var time = new FakeTimeProvider();
        var q = new InMemoryQueue("orders", capacity: 1, time);
        await using IAsyncEnumerator<InMemoryDelivery> consumer = await StartConsumerAsync(q, TestContext.Current.CancellationToken);

        ValueTask<QueueWaitResult> wait = q.WaitToReserveAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(100));
        (await wait).Should().Be(QueueWaitResult.TimedOut);
        int occupancyBeforeRelease = q.Occupancy;

        q.ReleaseSlot();

        q.Occupancy.Should().Be(occupancyBeforeRelease - 1);
    }

    [Fact]
    public async Task WaitToReserveAsync_Cancelled_ReturnsCancelledWithoutThrowing()
    {
        var q = new InMemoryQueue("orders", capacity: 1, new FakeTimeProvider());
        await using IAsyncEnumerator<InMemoryDelivery> consumer = await StartConsumerAsync(q, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        ValueTask<QueueWaitResult> wait = q.WaitToReserveAsync(TimeSpan.FromSeconds(1), cts.Token);
        await cts.CancelAsync();

        (await wait).Should().Be(QueueWaitResult.Cancelled);
    }

    [Fact]
    public async Task WaitToReserveAsync_ZeroTimeoutOnFullQueue_ReturnsTimedOutImmediately()
    {
        var q = new InMemoryQueue("orders", capacity: 1);
        await using IAsyncEnumerator<InMemoryDelivery> consumer = await StartConsumerAsync(q, TestContext.Current.CancellationToken);

        (await q.WaitToReserveAsync(TimeSpan.Zero, TestContext.Current.CancellationToken))
            .Should().Be(QueueWaitResult.TimedOut);
        q.HasPendingSpaceWaiter.Should().BeFalse();
    }

    [Fact]
    public async Task WaitToReserveAsync_NegativeTimeout_ThrowsArgumentOutOfRangeException()
    {
        var q = new InMemoryQueue("orders", capacity: 1);

        Func<Task> act = async () =>
            await q.WaitToReserveAsync(TimeSpan.FromMilliseconds(-1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task WaitToReserveAsync_FourWaitersOnCapacityOne_AllEventuallyReserve()
    {
        var time = new FakeTimeProvider();
        var q = new InMemoryQueue("orders", capacity: 1, time);
        await using IAsyncEnumerator<InMemoryDelivery> consumer = await StartConsumerAsync(q, TestContext.Current.CancellationToken);

        for (int i = 0; i < 4; i++)
        {
            ValueTask<QueueWaitResult> wait = q.WaitToReserveAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            q.ReleaseSlot();

            (await wait).Should().Be(QueueWaitResult.Reserved);
            q.WriteReserved(Delivery());
            (await consumer.MoveNextAsync()).Should().BeTrue(); // drain so the channel has room for the next write
        }

        q.IsLatched.Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseSlot_WithWaiters_HandsSlotToOldestWaiterWithoutWakingOthers()
    {
        var time = new FakeTimeProvider();
        var q = new InMemoryQueue("orders", capacity: 1, time);
        await using IAsyncEnumerator<InMemoryDelivery> consumer = await StartConsumerAsync(q, TestContext.Current.CancellationToken);

        ValueTask<QueueWaitResult> first = q.WaitToReserveAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        ValueTask<QueueWaitResult> second = q.WaitToReserveAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        ValueTask<QueueWaitResult> third = q.WaitToReserveAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        q.ReleaseSlot();

        (await first).Should().Be(QueueWaitResult.Reserved);
        second.IsCompleted.Should().BeFalse();
        third.IsCompleted.Should().BeFalse();
        q.Occupancy.Should().Be(1); // the slot was handed directly to the first waiter, not released

        // clean up the still-pending waiters so the test does not leak background work
        time.Advance(TimeSpan.FromSeconds(1));
        (await second).Should().Be(QueueWaitResult.TimedOut);
        (await third).Should().Be(QueueWaitResult.TimedOut);
    }
}
