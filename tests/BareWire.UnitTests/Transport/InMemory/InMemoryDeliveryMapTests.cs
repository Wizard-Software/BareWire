using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryDeliveryMapTests
{
    [Fact]
    public void UnsettledCount_TracksEveryRemovalPath()
    {
        // Arrange — two runners over the same map: three deliveries owned by A, one by B.
        var map = new InMemoryDeliveryMap();
        InMemoryQueue queue = NewQueue();
        InMemoryQueueRunner a = NewRunner(queue, map);
        InMemoryQueueRunner b = NewRunner(queue, map);
        ulong[] aTags = [Add(map, queue, a, disposed: false), Add(map, queue, a, disposed: false), Add(map, queue, a, disposed: true)];
        Add(map, queue, b, disposed: false);
        a.UnsettledCount.Should().Be(3);
        b.UnsettledCount.Should().Be(1);

        // Act / Assert — a settlement removal decrements its owner once, a repeated take changes nothing.
        map.TryTake(aTags[0], out _).Should().BeTrue();
        a.UnsettledCount.Should().Be(2);
        map.TryTake(aTags[0], out _).Should().BeFalse();
        a.UnsettledCount.Should().Be(2);
        b.UnsettledCount.Should().Be(1);

        // The disposed-by-consumer sweep.
        map.TakeReleasedByConsumer().Should().ContainSingle();
        a.UnsettledCount.Should().Be(1);

        // The shutdown sweep takes everything that is left.
        map.TakeAll().Should().HaveCount(2);
        a.UnsettledCount.Should().Be(0);
        b.UnsettledCount.Should().Be(0);
    }

    private static InMemoryQueue NewQueue() => new("orders", capacity: 16);

    private static InMemoryQueueRunner NewRunner(InMemoryQueue queue, InMemoryDeliveryMap map) =>
        new(queue, map, singleActiveGate: null, new InMemoryConsumeDiagnostics(NullLogger.Instance),
            new InMemoryBufferPool(), CancellationToken.None);

    // disposed = true models a message the consumer disposed without settling (PooledBuffer is null).
    private static ulong Add(InMemoryDeliveryMap map, InMemoryQueue queue, InMemoryQueueRunner owner, bool disposed)
    {
        ulong tag = map.NextTag();
        var delivery = new InMemoryDelivery(new byte[4], 4);
        var message = new InboundMessage(
            "m-" + tag, new Dictionary<string, string>(), new ReadOnlySequence<byte>(delivery.Body), tag,
            pooledBuffer: disposed ? null : delivery.Buffer);
        map.Add(tag, new InFlightDelivery(delivery, queue, owner, message));
        return tag;
    }
}
