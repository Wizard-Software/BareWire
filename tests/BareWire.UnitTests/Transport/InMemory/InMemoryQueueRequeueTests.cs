using AwesomeAssertions;
using BareWire.Transport.InMemory.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryQueueRequeueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static InMemoryDelivery Delivery(byte marker) => new([marker], 1);

    private static void Enqueue(InMemoryQueue queue, InMemoryDelivery delivery)
    {
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        queue.WriteReserved(delivery);
    }

    [Fact]
    public async Task RequeueAtHead_WithDeliveriesInChannel_PutsRequeuedAheadInOrder()
    {
        var queue = new InMemoryQueue("orders", 8);
        foreach (byte marker in new byte[] { 1, 2, 3 })
        {
            Enqueue(queue, Delivery(marker));
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IAsyncEnumerator<InMemoryDelivery> reader = queue.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        (await reader.MoveNextAsync()).Should().BeTrue();
        InMemoryDelivery first = reader.Current;

        queue.RequeueAtHead([first]);

        var order = new List<byte>();
        for (int i = 0; i < 3; i++)
        {
            (await reader.MoveNextAsync().AsTask().WaitAsync(Timeout, ct)).Should().BeTrue();
            order.Add(reader.Current.Buffer[0]);
        }

        order.Should().Equal(1, 2, 3);
        queue.Occupancy.Should().Be(3);
    }

    [Fact]
    public async Task RequeueAtHead_WithSeveralDeliveries_KeepsTheirRelativeOrderAheadOfTheChannel()
    {
        var queue = new InMemoryQueue("orders", 8);
        foreach (byte marker in new byte[] { 1, 2, 3, 4 })
        {
            Enqueue(queue, Delivery(marker));
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IAsyncEnumerator<InMemoryDelivery> reader = queue.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        (await reader.MoveNextAsync()).Should().BeTrue();
        InMemoryDelivery first = reader.Current;
        (await reader.MoveNextAsync()).Should().BeTrue();
        InMemoryDelivery second = reader.Current;

        queue.RequeueAtHead([first, second]);

        var order = new List<byte>();
        for (int i = 0; i < 4; i++)
        {
            (await reader.MoveNextAsync().AsTask().WaitAsync(Timeout, ct)).Should().BeTrue();
            order.Add(reader.Current.Buffer[0]);
        }

        order.Should().Equal(1, 2, 3, 4);
        queue.Occupancy.Should().Be(4);
    }

    [Fact]
    public async Task RequeueAtHead_WhenReaderIsWaitingOnEmptyChannel_WakesTheReader()
    {
        var queue = new InMemoryQueue("orders", 4);
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        InMemoryDelivery held = Delivery(7);

        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IAsyncEnumerator<InMemoryDelivery> reader = queue.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        ValueTask<bool> pending = reader.MoveNextAsync();

        queue.RequeueAtHead([held]);

        (await pending.AsTask().WaitAsync(Timeout, ct)).Should().BeTrue();
        reader.Current.Should().BeSameAs(held);
        queue.Occupancy.Should().Be(1);
    }

    [Fact]
    public void RequeueAtHead_EmptyList_LeavesQueueUnchanged()
    {
        var queue = new InMemoryQueue("orders", 4);
        Enqueue(queue, Delivery(1));

        queue.RequeueAtHead([]);

        queue.Occupancy.Should().Be(1);
    }

    [Fact]
    public void RequeueAtHead_NullList_ThrowsArgumentNullException()
    {
        var queue = new InMemoryQueue("orders", 4);

        Action act = () => queue.RequeueAtHead(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task RequeueAtHead_MoreDeliveriesThanCapacity_ThrowsAndKeepsDrainedDeliveries()
    {
        var queue = new InMemoryQueue("orders", 2);
        Enqueue(queue, Delivery(1));
        Enqueue(queue, Delivery(2));

        // Two requeued deliveries without reservations on a full queue: the invariant is violated.
        Action act = () => queue.RequeueAtHead([Delivery(8), Delivery(9)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*orders*");

        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IAsyncEnumerator<InMemoryDelivery> reader = queue.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        var order = new List<byte>();
        for (int i = 0; i < 2; i++)
        {
            (await reader.MoveNextAsync().AsTask().WaitAsync(Timeout, ct)).Should().BeTrue();
            order.Add(reader.Current.Buffer[0]);
        }

        order.Should().Equal(1, 2);
    }
}
