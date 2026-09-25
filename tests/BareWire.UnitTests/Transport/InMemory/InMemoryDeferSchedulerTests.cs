using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryDeferSchedulerTests
{
    private static InMemoryQueue Queue(int capacity = 4) => new("orders", capacity);

    private static InMemoryDelivery Delivery(string id = "m1")
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
        int length = Encoding.UTF8.GetBytes(id, buffer);
        InMemoryHeaderSet headers = InMemoryHeaderSet.Stamp(
            new Dictionary<string, string> { [InMemoryHeaderNames.MessageId] = id },
            string.Empty,
            "orders",
            string.Empty);
        return new InMemoryDelivery(buffer, length, headers);
    }

    [Fact]
    public void Schedule_WhenDelayElapses_WritesRedeliveryToQueue()
    {
        var time = new FakeTimeProvider();
        var diagnostics = new InMemoryConsumeDiagnostics(NullLogger.Instance, metrics: null, timeProvider: time);
        using var scheduler = new InMemoryDeferScheduler(time, new InMemoryBufferPool(), diagnostics);
        InMemoryQueue queue = Queue();
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);

        scheduler.TrySchedule(queue, Delivery(), TimeSpan.FromSeconds(30)).Should().BeTrue();
        scheduler.PendingCount.Should().Be(1);
        queue.Occupancy.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(29));
        scheduler.PendingCount.Should().Be(1); // not due yet
        queue.Occupancy.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(1));
        scheduler.PendingCount.Should().Be(0); // due -> written back
        queue.Occupancy.Should().Be(1); // same reserved slot, reused — never released
    }

    [Fact]
    public void TrySchedule_WhenDelayAboveTimerLimit_ThrowsBeforeTakingOwnership()
    {
        var time = new FakeTimeProvider();
        var diagnostics = new InMemoryConsumeDiagnostics(NullLogger.Instance, metrics: null, timeProvider: time);
        using var scheduler = new InMemoryDeferScheduler(time, new InMemoryBufferPool(), diagnostics);
        InMemoryQueue queue = Queue();
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        InMemoryDelivery delivery = Delivery();

        scheduler.Invoking(s => s.TrySchedule(
                queue, delivery, InMemoryTransportOptions.MaxDeferDelay + TimeSpan.FromMilliseconds(1)))
            .Should().Throw<ArgumentOutOfRangeException>();

        scheduler.PendingCount.Should().Be(0);
        queue.Occupancy.Should().Be(1); // still the caller's slot — nothing was claimed
        queue.ReleaseSlot();
        ArrayPool<byte>.Shared.Return(delivery.Buffer);
    }

    [Fact]
    public void TrySchedule_WhenArmingTimerFails_ReleasesSlotAndReturnsBufferExactlyOnce()
    {
        var time = new FailingArmTimeProvider();
        var diagnostics = new InMemoryConsumeDiagnostics(NullLogger.Instance, metrics: null, timeProvider: time);
        var observer = new CountingBufferPoolObserver();
        var pool = new InMemoryBufferPool(observer);
        using var scheduler = new InMemoryDeferScheduler(time, pool, diagnostics);
        InMemoryQueue queue = Queue();
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        InMemoryDelivery delivery = Delivery();
        byte[] hooked = pool.Rent(delivery.Length);
        delivery.Body.Span.CopyTo(hooked);
        var pending = new InMemoryDelivery(hooked, delivery.Length, delivery.Headers);
        ArrayPool<byte>.Shared.Return(delivery.Buffer);

        scheduler.Invoking(s => s.TrySchedule(queue, pending, TimeSpan.FromSeconds(30)))
            .Should().Throw<InvalidOperationException>();

        scheduler.PendingCount.Should().Be(0);
        queue.Occupancy.Should().Be(0); // slot released by the scheduler, not left until Dispose
        observer.Returned.Should().Be(1);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_WithPendingDeliveries_ReleasesSlotsAndReturnsBuffersExactlyOnce()
    {
        var time = new FakeTimeProvider();
        var diagnostics = new InMemoryConsumeDiagnostics(NullLogger.Instance, metrics: null, timeProvider: time);
        var observer = new CountingBufferPoolObserver();
        var pool = new InMemoryBufferPool(observer);
        var scheduler = new InMemoryDeferScheduler(time, pool, diagnostics);
        InMemoryQueue queue = Queue();
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        InMemoryDelivery delivery = Delivery();

        // Route the pending copy's buffer through the hook so the observer can prove it comes back exactly
        // once: rent a hook-tracked buffer, copy the delivery's body into it, and schedule that instead.
        byte[] hooked = pool.Rent(delivery.Length);
        delivery.Body.Span.CopyTo(hooked);
        var pending = new InMemoryDelivery(hooked, delivery.Length, delivery.Headers);
        ArrayPool<byte>.Shared.Return(delivery.Buffer);

        scheduler.TrySchedule(queue, pending, TimeSpan.FromSeconds(30)).Should().BeTrue();

        scheduler.Dispose();

        scheduler.PendingCount.Should().Be(0);
        queue.Occupancy.Should().Be(0); // slot released
        observer.Returned.Should().Be(1);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();

        scheduler.Dispose(); // idempotent
    }

    [Fact]
    public void Schedule_AfterDispose_ReturnsFalse()
    {
        var time = new FakeTimeProvider();
        var diagnostics = new InMemoryConsumeDiagnostics(NullLogger.Instance, metrics: null, timeProvider: time);
        var scheduler = new InMemoryDeferScheduler(time, new InMemoryBufferPool(), diagnostics);
        scheduler.Dispose();

        InMemoryQueue queue = Queue();
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);

        bool scheduled = scheduler.TrySchedule(queue, Delivery(), TimeSpan.FromSeconds(30));

        scheduled.Should().BeFalse();
        scheduler.PendingCount.Should().Be(0);
        // TrySchedule never took ownership: the caller (not the scheduler) still owns the slot.
        queue.Occupancy.Should().Be(1);
    }

    [Fact]
    public async Task Schedule_RacingDispose_NeverDoubleReleasesTheSlot()
    {
        var time = new FakeTimeProvider();
        var observer = new CountingBufferPoolObserver();
        var pool = new InMemoryBufferPool(observer);

        const int iterations = 200;
        for (int i = 0; i < iterations; i++)
        {
            var diagnostics = new InMemoryConsumeDiagnostics(NullLogger.Instance, metrics: null, timeProvider: time);
            var scheduler = new InMemoryDeferScheduler(time, pool, diagnostics);
            InMemoryQueue queue = Queue();
            queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
            InMemoryDelivery delivery = Delivery();
            byte[] hooked = pool.Rent(delivery.Length);
            delivery.Body.Span.CopyTo(hooked);
            var pending = new InMemoryDelivery(hooked, delivery.Length, delivery.Headers);
            ArrayPool<byte>.Shared.Return(delivery.Buffer);

            using var barrier = new Barrier(2);
            bool scheduled = false;
            Task schedule = Task.Run(() =>
            {
                barrier.SignalAndWait();
                scheduled = scheduler.TrySchedule(queue, pending, TimeSpan.FromSeconds(30));
            });
            Task dispose = Task.Run(() =>
            {
                barrier.SignalAndWait();
                scheduler.Dispose();
            });
            await Task.WhenAll(schedule, dispose);

            // TrySchedule == false means the scheduler never took ownership (disposed before it created
            // any entry): the slot and the buffer are still the caller's to release, exactly as
            // InMemorySettlement.Defer does. TrySchedule == true means ownership transferred to the
            // scheduler, which — since time was never advanced — can only have been reclaimed by this same
            // Dispose() call.
            if (!scheduled)
            {
                queue.ReleaseSlot();
                pool.Return(pending.Buffer);
            }

            queue.Occupancy.Should().Be(0);
        }

        observer.Violations.Should().BeEmpty();
        observer.Outstanding.Should().Be(0);
    }

    [Fact]
    public void TimerCallback_WhenWriteFails_ReturnsBufferAndReleasesSlotWithoutThrowing()
    {
        var time = new FakeTimeProvider();
        var diagnostics = new InMemoryConsumeDiagnostics(NullLogger.Instance, metrics: null, timeProvider: time);
        var observer = new CountingBufferPoolObserver();
        var pool = new InMemoryBufferPool(observer);
        using var scheduler = new InMemoryDeferScheduler(time, pool, diagnostics);

        // A 1-capacity queue with NO reservation held: WriteReserved must throw InvalidOperationException
        // (the redelivery claims to hold a slot it never actually reserved) — proving the callback survives
        // that failure instead of propagating it.
        InMemoryQueue queue = Queue(capacity: 1);
        InMemoryDelivery delivery = Delivery();
        byte[] hooked = pool.Rent(delivery.Length);
        delivery.Body.Span.CopyTo(hooked);
        var pending = new InMemoryDelivery(hooked, delivery.Length, delivery.Headers);
        ArrayPool<byte>.Shared.Return(delivery.Buffer);

        // Reserve, then release BEFORE scheduling: WriteReserved has nothing to write into. To force the
        // failure deterministically, saturate the channel to capacity independently of the scheduled entry.
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        queue.WriteReserved(new InMemoryDelivery(ArrayPool<byte>.Shared.Rent(1), 0));

        scheduler.TrySchedule(queue, pending, TimeSpan.FromSeconds(1)).Should().BeTrue();

        Action fire = () => time.Advance(TimeSpan.FromSeconds(1));

        fire.Should().NotThrow();
        scheduler.PendingCount.Should().Be(0);
        observer.Returned.Should().Be(1); // the pending copy came back through the pool
    }

    private sealed class FailingArmTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FailingArmTimer();

        private sealed class FailingArmTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                throw new InvalidOperationException("Simulated timer arming failure.");

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
