using AwesomeAssertions;
using BareWire.Transport.InMemory.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryQueueCloseTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    private static byte[] WriteFromPool(InMemoryQueue queue, InMemoryBufferPool pool)
    {
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        byte[] buffer = pool.Rent(8);
        queue.WriteReserved(new InMemoryDelivery(buffer, 8));
        return buffer;
    }

    [Fact]
    public async Task WaitToReserveAsync_WhenQueueClosedWhileWaiting_ReturnsClosedWithoutSlot()
    {
        var queue = new InMemoryQueue("orders", capacity: 1);
        using var consumerCts = new CancellationTokenSource();
        IAsyncEnumerator<InMemoryDelivery> reader = queue.ReadAllAsync(consumerCts.Token).GetAsyncEnumerator();
        Task<bool> pendingMove = reader.MoveNextAsync().AsTask();          // registers an active consumer
        await WaitUntilAsync(() => queue.HasActiveConsumer);
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);  // the queue is full now
        Task<QueueWaitResult> wait = queue.WaitToReserveAsync(TimeSpan.FromMinutes(5)).AsTask();
        await WaitUntilAsync(() => queue.HasPendingSpaceWaiter);

        queue.Close(new InMemoryBufferPool());

        (await wait.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(QueueWaitResult.Closed);
        queue.HasPendingSpaceWaiter.Should().BeFalse();
        queue.LinkedWaiterCount.Should().Be(0);
        queue.Occupancy.Should().Be(1);        // the closed waiter never took a slot
        queue.ReleaseSlot();
        queue.Occupancy.Should().Be(0);        // _waiterCount consistent: no hand-off to a ghost waiter

        await consumerCts.CancelAsync();
        await FluentActions.Awaiting(() => pendingMove).Should().ThrowAsync<OperationCanceledException>();
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task WaitToReserveAsync_WhenQueueAlreadyClosed_ReturnsClosedWithoutEnqueuingWaiter()
    {
        var queue = new InMemoryQueue("orders", capacity: 1);
        queue.Close(new InMemoryBufferPool());

        (await queue.WaitToReserveAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken))
            .Should().Be(QueueWaitResult.Closed);
        queue.LinkedWaiterCount.Should().Be(0);
        queue.HasPendingSpaceWaiter.Should().BeFalse();
    }

    [Fact]
    public void DropRemaining_WithQueuedDeliveries_ReturnsEveryBufferOnceAndReleasesSlots()
    {
        var observer = new CountingBufferPoolObserver();
        var pool = new InMemoryBufferPool(observer);
        var queue = new InMemoryQueue("orders", capacity: 4);
        for (int i = 0; i < 3; i++)
        {
            WriteFromPool(queue, pool);
        }

        queue.Close(pool);

        queue.DropRemaining().Should().Be(3);
        queue.Occupancy.Should().Be(0);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
        queue.DropRemaining().Should().Be(0);
        queue.TakeDroppedAfterClose().Should().Be(3);
        queue.TakeDroppedAfterClose().Should().Be(0);
    }

    [Fact]
    public void WriteReserved_AfterClose_DropsDeliveryWithoutThrowing()
    {
        var observer = new CountingBufferPoolObserver();
        var pool = new InMemoryBufferPool(observer);
        var queue = new InMemoryQueue("orders", capacity: 4);
        queue.Close(pool);

        Action act = () => WriteFromPool(queue, pool);

        act.Should().NotThrow();
        queue.Occupancy.Should().Be(0);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
        queue.TakeDroppedAfterClose().Should().Be(1);
    }

    [Fact]
    public void RequeueAtHead_AfterClose_DropsDeliveryWithoutThrowing()
    {
        var observer = new CountingBufferPoolObserver();
        var pool = new InMemoryBufferPool(observer);
        var queue = new InMemoryQueue("orders", capacity: 4);
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        byte[] buffer = pool.Rent(8);
        queue.Close(pool);

        Action act = () => queue.RequeueAtHead(new InMemoryDelivery(buffer, 8));

        act.Should().NotThrow();
        queue.Occupancy.Should().Be(0);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
        queue.TakeDroppedAfterClose().Should().Be(1);
    }
}
