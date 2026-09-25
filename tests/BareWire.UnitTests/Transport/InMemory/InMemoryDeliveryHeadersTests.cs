using AwesomeAssertions;

using BareWire.Transport.InMemory.Internal;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Tests for the header-carrying side of <see cref="InMemoryDelivery"/>: sharing one
/// <see cref="InMemoryHeaderSet"/> instance across every fan-out copy of a publish, the redelivery
/// counter held in a delivery field (never read back from a header), the
/// <see cref="RedeliveryHeaderOverlay"/> materialized only when that counter is greater than zero, and
/// the allocation budget of the header path.
/// </summary>
public sealed class InMemoryDeliveryHeadersTests
{
    private static InMemoryHeaderSet Stamped() =>
        InMemoryHeaderSet.Stamp(
            new Dictionary<string, string>
            {
                ["message-id"] = "m-1",
                ["BW-RedeliveryCount"] = "999",
                ["bw-redeliverycount"] = "999",
            },
            "fanout-x", "rk", "");

    [Fact]
    public void FanOutCopies_ShareOneHeaderSetInstance()
    {
        InMemoryHeaderSet set = Stamped();
        InMemoryDelivery[] copies = [new(new byte[4], 4, set), new(new byte[4], 4, set), new(new byte[4], 4, set)];

        foreach (InMemoryDelivery copy in copies)
        {
            copy.InboundHeaders.Should().BeSameAs(set);
            copy.MessageId.Should().BeSameAs(set.MessageId);
        }
    }

    [Fact]
    public void InboundHeaders_FirstDelivery_HasNoRedeliveryHeaderEvenWhenPublisherSpoofedIt()
    {
        var delivery = new InMemoryDelivery(new byte[4], 4, Stamped());

        delivery.RedeliveryCount.Should().Be(0);
        delivery.InboundHeaders.ContainsKey("BW-RedeliveryCount").Should().BeFalse();
        delivery.InboundHeaders.Keys.Should().NotContain(
            key => string.Equals(key, InMemoryHeaderNames.RedeliveryCount, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateRedelivery_IncrementsFieldAndMaterializesMatchingHeader()
    {
        InMemoryHeaderSet set = Stamped();
        var first = new InMemoryDelivery(new byte[4], 4, set);

        InMemoryDelivery second = first.CreateRedelivery(new byte[4], 4);
        InMemoryDelivery third = second.CreateRedelivery(new byte[4], 4);

        second.RedeliveryCount.Should().Be(1);
        second.InboundHeaders["BW-RedeliveryCount"].Should().Be("1");
        second.InboundHeaders.Keys.Count(
            key => string.Equals(key, InMemoryHeaderNames.RedeliveryCount, StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
        third.RedeliveryCount.Should().Be(2);
        third.InboundHeaders["BW-RedeliveryCount"].Should().Be("2");
        third.Headers.Should().BeSameAs(set);
        third.MessageId.Should().Be(first.MessageId); // stable identity across redeliveries
        first.RedeliveryCount.Should().Be(0); // the original delivery is never mutated
    }

    [Fact]
    public void RedeliveryOverlay_ExposesBaseEntriesPlusCounter()
    {
        InMemoryHeaderSet set = Stamped();
        var delivery = new InMemoryDelivery(new byte[4], 4, set, redeliveryCount: 3);
        IReadOnlyDictionary<string, string> headers = delivery.InboundHeaders;

        headers.Count.Should().Be(set.Count + 1);
        headers["BW-Exchange"].Should().Be("fanout-x");
        headers["message-id"].Should().Be("m-1");
        headers.Select(kv => kv.Key).Should().Equal(set.Keys.Append("BW-RedeliveryCount"));
        headers.TryGetValue("absent", out _).Should().BeFalse();
        FluentActions.Invoking(() => headers["absent"]).Should().Throw<KeyNotFoundException>();
        delivery.InboundHeaders.Should().BeSameAs(delivery.InboundHeaders);
    }

    [Fact]
    public void Constructor_DefaultsToEmptyHeadersAndRejectsNegativeCount()
    {
        var legacy = new InMemoryDelivery(new byte[4], 4);

        legacy.Headers.Should().BeSameAs(InMemoryHeaderSet.Empty);
        legacy.MessageId.Should().BeEmpty();
        FluentActions.Invoking(() => new InMemoryDelivery(new byte[4], 4, null, redeliveryCount: -1))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new RedeliveryHeaderOverlay(InMemoryHeaderSet.Empty, 0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HeaderPath_FanOutOfSixteenCopies_AllocatesNothingPerCopy()
    {
        InMemoryHeaderSet set = Stamped();
        var copies = new InMemoryDelivery[16];
        for (int i = 0; i < copies.Length; i++)
        {
            copies[i] = new InMemoryDelivery(new byte[4], 4, set);
        }

        // Warm-up executes the whole measured loop body (both InboundHeaders and MessageId reads on
        // every copy) so neither JIT nor a first-read side effect leaks into the measured delta.
        for (int warmUp = 0; warmUp < 100; warmUp++)
        {
            for (int i = 0; i < copies.Length; i++)
            {
                _ = copies[i].InboundHeaders;
                _ = copies[i].MessageId;
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < copies.Length; i++)
        {
            _ = copies[i].InboundHeaders;
            _ = copies[i].MessageId;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0); // one shared set per publish — no Dictionary (or any object) per copy
    }
}
