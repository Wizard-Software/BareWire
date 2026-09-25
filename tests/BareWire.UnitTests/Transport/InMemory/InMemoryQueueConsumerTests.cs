using AwesomeAssertions;
using BareWire.Transport.InMemory.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryQueueConsumerTests
{
    private static InMemoryDelivery Delivery() => new(new byte[1], 1);

    private static void Enqueue(InMemoryQueue q)
    {
        q.TryReserve().Should().Be(QueueReservationResult.Reserved);
        q.WriteReserved(Delivery());
    }

    [Fact]
    public async Task ReadAllAsync_EndpointLoopFaults_StopsCountingAsActive()
    {
        var q = new InMemoryQueue("orders", capacity: 4);
        Enqueue(q);

        Func<Task> loop = async () =>
        {
            await foreach (InMemoryDelivery _ in q.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                q.HasActiveConsumer.Should().BeTrue();
                throw new InvalidOperationException("handler loop fault");
            }
        };

        await loop.Should().ThrowAsync<InvalidOperationException>();
        q.HasActiveConsumer.Should().BeFalse();
        q.ActiveConsumerCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadAllAsync_HandlerHangs_StillCountsAsActive()
    {
        var q = new InMemoryQueue("orders", capacity: 4);
        Enqueue(q);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task loop = Task.Run(async () =>
        {
            await foreach (InMemoryDelivery _ in q.ReadAllAsync(cts.Token))
            {
                entered.TrySetResult();
                await release.Task; // hung handler
            }
        }, TestContext.Current.CancellationToken);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        q.HasActiveConsumer.Should().BeTrue();
        release.SetResult();
        await cts.CancelAsync();

        Func<Task> awaitLoop = () => loop;
        await awaitLoop.Should().ThrowAsync<OperationCanceledException>();
        q.HasActiveConsumer.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAllAsync_Cancelled_StopsCountingBeforeEnumeratorIsDisposed()
    {
        var q = new InMemoryQueue("orders", capacity: 4);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        IAsyncEnumerator<InMemoryDelivery> e = q.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Enqueue(q);

        (await e.MoveNextAsync()).Should().BeTrue(); // enumerator suspended at yield (handler running)
        q.HasActiveConsumer.Should().BeTrue();
        await cts.CancelAsync();

        q.HasActiveConsumer.Should().BeFalse(); // cancelled reader no longer counts, enumerator not disposed yet
        await e.DisposeAsync();
        q.ActiveConsumerCount.Should().Be(0); // no double decrement
    }

    [Fact]
    public async Task ReadAllAsync_Cancelled_StopsReadingPendingDeliveries()
    {
        var q = new InMemoryQueue("orders", capacity: 4);
        Enqueue(q);
        Enqueue(q);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        IAsyncEnumerator<InMemoryDelivery> e = q.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        (await e.MoveNextAsync()).Should().BeTrue(); // first delivery read, second still pending in the channel
        int occupancyBeforeCancel = q.Occupancy;
        await cts.CancelAsync();

        Func<Task> moveNext = async () => await e.MoveNextAsync();
        await moveNext.Should().ThrowAsync<OperationCanceledException>();
        q.Occupancy.Should().Be(occupancyBeforeCancel); // the still-pending delivery was never touched
        await e.DisposeAsync();
    }

    [Fact]
    public async Task TryReserve_FullQueueAfterConsumerFaulted_LatchesInsteadOfOfferingWait()
    {
        var q = new InMemoryQueue("orders", capacity: 1);
        Enqueue(q);

        Func<Task> loop = async () =>
        {
            await foreach (InMemoryDelivery _ in q.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                throw new InvalidOperationException("handler loop fault");
            }
        };

        await loop.Should().ThrowAsync<InvalidOperationException>();
        q.TryReserve().Should().Be(QueueReservationResult.Latched); // full + no active consumer => never a reason to wait
    }

    [Fact]
    public async Task TryReserveAndReleaseSlot_ConcurrentDrainAndReserve_LatchNeverHangsOnEmptyQueue()
    {
        const int capacity = 8;
        const int iterations = 200;
        const int reserveAttempts = 500;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var q = new InMemoryQueue("orders", capacity);
            for (int i = 0; i < capacity; i++)
            {
                q.TryReserve().Should().Be(QueueReservationResult.Reserved);
                q.WriteReserved(new InMemoryDelivery([1], 1));
            }

            q.TryReserve().Should().Be(QueueReservationResult.Latched);

            Task drain = Task.Run(
                () =>
                {
                    for (int i = 0; i < capacity; i++)
                    {
                        q.ReleaseSlot();
                    }
                },
                TestContext.Current.CancellationToken);

            Task churn = Task.Run(
                () =>
                {
                    for (int i = 0; i < reserveAttempts; i++)
                    {
                        if (q.TryReserve() == QueueReservationResult.Reserved)
                        {
                            q.ReleaseSlot();
                        }
                    }
                },
                TestContext.Current.CancellationToken);

            await Task.WhenAll(drain, churn).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // "drain" always releases exactly `capacity` slots and "churn" pairs every successful
            // reservation with an immediate release, so the net effect on occupancy is always zero: the
            // queue must be empty and, critically, the latch must never have been left set once it was.
            q.Occupancy.Should().Be(0);
            q.TryReserve().Should().Be(QueueReservationResult.Reserved);
        }
    }

    [Fact]
    public async Task MultipleWriters_ConcurrentReserveWriteRelease_NoDeadlockAndNeverExceedsCapacity()
    {
        var q = new InMemoryQueue("orders", capacity: 16);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30)); // deadlock safety net, not the primary failure signal
        int maxObserved = 0, received = 0, accepted = 0, latchedSeen = 0;
        const int writers = 8, perWriter = 500;

        Task consumer = Task.Run(
            async () =>
            {
                await foreach (InMemoryDelivery _ in q.ReadAllAsync(cts.Token))
                {
                    InterlockedMax(ref maxObserved, q.Occupancy);
                    q.ReleaseSlot();
                    if (Interlocked.Increment(ref received) == writers * perWriter)
                    {
                        return;
                    }
                }
            },
            TestContext.Current.CancellationToken);

        // wait until the consumer is registered before writing
        SpinWait.SpinUntil(() => q.HasActiveConsumer, TimeSpan.FromSeconds(5)).Should().BeTrue();

        Task[] producers = Enumerable.Range(0, writers).Select(_ => Task.Run(
            async () =>
            {
                try
                {
                    for (int i = 0; i < perWriter;)
                    {
                        switch (q.TryReserve())
                        {
                            case QueueReservationResult.Reserved:
                                q.WriteReserved(new InMemoryDelivery(new byte[1], 1));
                                InterlockedMax(ref maxObserved, q.Occupancy);
                                Interlocked.Increment(ref accepted);
                                i++;
                                break;
                            case QueueReservationResult.Full:
                                QueueWaitResult waitResult = await q.WaitToReserveAsync(
                                    TimeSpan.FromMilliseconds(50), cts.Token);
                                if (waitResult == QueueWaitResult.Reserved)
                                {
                                    q.WriteReserved(new InMemoryDelivery(new byte[1], 1));
                                    InterlockedMax(ref maxObserved, q.Occupancy);
                                    Interlocked.Increment(ref accepted);
                                    i++;
                                }
                                else if (waitResult == QueueWaitResult.Latched)
                                {
                                    Interlocked.Increment(ref latchedSeen);
                                }

                                break;
                            default: // Latched
                                Interlocked.Increment(ref latchedSeen);
                                await Task.Yield();
                                break;
                        }
                    }
                }
                catch
                {
                    // fail fast: cancel every other producer and the consumer instead of waiting out the
                    // full 30-second safety net when a real bug (e.g. a broken CAS loop) has fired.
                    await cts.CancelAsync();
                    throw;
                }
            },
            TestContext.Current.CancellationToken)).ToArray();

        try
        {
            await Task.WhenAll(producers);
        }
        finally
        {
            try
            {
                await consumer;
            }
            catch (OperationCanceledException)
            {
                // expected once a producer failure cancels the shared token
            }
        }

        maxObserved.Should().BeLessThanOrEqualTo(16);
        received.Should().Be(accepted).And.Be(writers * perWriter);
        q.Occupancy.Should().Be(0);
        q.IsLatched.Should().BeFalse();
        latchedSeen.Should().Be(0); // the consumer stays active for the whole run, so the latch can never engage
    }

    [Fact]
    public async Task MultipleWriters_ShortTimeoutsRaceGrantAgainstTimeout_NoLeakedOrDoubleCountedSlots()
    {
        // Short, jittered real-time timeouts deliberately race a ReleaseSlot hand-off against a
        // WaitToReserveAsync timeout on the same waiter, exercising the mutually exclusive
        // TryMarkGranted/TryMarkAbandoned compare-and-swap under genuine contention (not just the
        // single-threaded scenarios in InMemoryQueueTests).
        var q = new InMemoryQueue("orders", capacity: 4);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30)); // deadlock safety net, not the primary failure signal
        int received = 0, accepted = 0;
        const int writers = 6, perWriter = 300;

        Task consumer = Task.Run(
            async () =>
            {
                await foreach (InMemoryDelivery _ in q.ReadAllAsync(cts.Token))
                {
                    q.ReleaseSlot();
                    if (Interlocked.Increment(ref received) == writers * perWriter)
                    {
                        return;
                    }
                }
            },
            TestContext.Current.CancellationToken);

        // wait until the consumer is registered before writing
        SpinWait.SpinUntil(() => q.HasActiveConsumer, TimeSpan.FromSeconds(5)).Should().BeTrue();

        Task[] producers = Enumerable.Range(0, writers).Select(_ => Task.Run(
            async () =>
            {
                try
                {
                    for (int i = 0; i < perWriter;)
                    {
                        switch (q.TryReserve())
                        {
                            case QueueReservationResult.Reserved:
                                q.WriteReserved(new InMemoryDelivery(new byte[1], 1));
                                Interlocked.Increment(ref accepted);
                                i++;
                                break;
                            case QueueReservationResult.Full:
                                TimeSpan waitTimeout = TimeSpan.FromMilliseconds(Random.Shared.Next(1, 6));
                                QueueWaitResult waitResult = await q.WaitToReserveAsync(waitTimeout, cts.Token);
                                if (waitResult == QueueWaitResult.Reserved)
                                {
                                    q.WriteReserved(new InMemoryDelivery(new byte[1], 1));
                                    Interlocked.Increment(ref accepted);
                                    i++;
                                }

                                break;
                            default: // Latched: the consumer stays active for the whole run, so this never happens
                                await Task.Yield();
                                break;
                        }
                    }
                }
                catch
                {
                    // fail fast: cancel every other producer and the consumer instead of waiting out the
                    // full 30-second safety net when a real bug (e.g. a leaked or double-granted slot) has
                    // fired.
                    await cts.CancelAsync();
                    throw;
                }
            },
            TestContext.Current.CancellationToken)).ToArray();

        try
        {
            await Task.WhenAll(producers);
        }
        finally
        {
            try
            {
                await consumer;
            }
            catch (OperationCanceledException)
            {
                // expected once a producer failure cancels the shared token
            }
        }

        received.Should().Be(accepted).And.Be(writers * perWriter);
        q.Occupancy.Should().Be(0); // every reserved slot was eventually released or drained — nothing leaked
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
