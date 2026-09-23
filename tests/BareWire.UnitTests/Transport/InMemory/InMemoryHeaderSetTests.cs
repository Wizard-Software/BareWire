using System.Collections.ObjectModel;

using AwesomeAssertions;

using BareWire.Transport.InMemory.Internal;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Tests for <see cref="InMemoryHeaderSet.Stamp"/>: stripping every publisher-spoofed <c>BW-*</c>
/// header, stamping the authoritative routing headers, resolving <c>MessageId</c>, and the
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/> contract (including its allocation budget).
/// </summary>
public sealed class InMemoryHeaderSetTests
{
    // Warm-up iterations excluding JIT / tiered-compilation cost from the allocation measurement
    // (pattern mirrors CloudEventBinaryHeaderMapperAllocationTests).
    private const int WarmUpIterations = 100;

    [Fact]
    public void Stamp_PublisherSpoofsTransportHeaders_StripsThemAndStampsActualRoutingValues()
    {
        var publisher = new Dictionary<string, string>
        {
            ["message-id"] = "m-1",
            ["BW-Exchange"] = "spoofed-exchange",
            ["BW-RoutingKey"] = "spoofed.key",
            ["bw-exchange"] = "lower-case-spoof",
            ["BW-RedeliveryCount"] = "999",
            ["BW-ConsumerChannelId"] = "stolen-channel",
            ["x-tenant"] = "acme",
        };

        InMemoryHeaderSet set = InMemoryHeaderSet.Stamp(publisher, exchange: "orders", routingKey: "order.created", contentType: "");

        set["BW-Exchange"].Should().Be("orders");
        set["BW-RoutingKey"].Should().Be("order.created");
        set.ContainsKey("bw-exchange").Should().BeFalse();
        set.ContainsKey("BW-RedeliveryCount").Should().BeFalse();
        set.ContainsKey("BW-ConsumerChannelId").Should().BeFalse();
        set["x-tenant"].Should().Be("acme");
        set.Keys.Where(k => k.StartsWith("BW-", StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(["BW-Exchange", "BW-RoutingKey"]);
    }

    [Fact]
    public void Stamp_PublisherSetsMessageType_KeepsExactMessageTypeAndStripsOtherCasing()
    {
        var publisher = new Dictionary<string, string>
        {
            ["BW-MessageType"] = "Orders.OrderCreated",
            ["bw-messagetype"] = "Spoofed.Type",
        };

        InMemoryHeaderSet set = InMemoryHeaderSet.Stamp(publisher, "orders", "rk", "");

        set["BW-MessageType"].Should().Be("Orders.OrderCreated");
        set.ContainsKey("bw-messagetype").Should().BeFalse();
    }

    [Fact]
    public void Stamp_DefaultExchange_StampsEmptyExchange()
    {
        InMemoryHeaderSet set = InMemoryHeaderSet.Stamp(
            new Dictionary<string, string> { ["BW-Exchange"] = "", ["message-id"] = "m" }, "", "queue-a", "");

        set["BW-Exchange"].Should().BeEmpty();
        set["BW-RoutingKey"].Should().Be("queue-a");
    }

    [Fact]
    public void Stamp_PublisherMessageId_UsesSameStringInstanceWithoutGeneratingGuid()
    {
        string id = "publisher-id-1";
        InMemoryHeaderSet set = InMemoryHeaderSet.Stamp(new Dictionary<string, string> { ["message-id"] = id }, "x", "rk", "");

        set.MessageId.Should().BeSameAs(id);
        set["message-id"].Should().BeSameAs(id);
        set.Count(kv => kv.Key == "message-id").Should().Be(1);
    }

    [Fact]
    public void Stamp_MissingOrEmptyMessageId_GeneratesOneGuidAndStampsItAsMessageId()
    {
        InMemoryHeaderSet missing = InMemoryHeaderSet.Stamp(new Dictionary<string, string>(), "x", "rk", "");
        InMemoryHeaderSet empty = InMemoryHeaderSet.Stamp(new Dictionary<string, string> { ["message-id"] = "" }, "x", "rk", "");

        Guid.TryParse(missing.MessageId, out _).Should().BeTrue();
        missing["message-id"].Should().BeSameAs(missing.MessageId);
        Guid.TryParse(empty.MessageId, out _).Should().BeTrue();
        empty.Count(kv => kv.Key == "message-id").Should().Be(1);
        empty.MessageId.Should().NotBe(missing.MessageId);
    }

    [Fact]
    public void Stamp_ContentTypeSupplied_OverridesPublisherContentType()
    {
        var publisher = new Dictionary<string, string> { ["message-id"] = "m", ["content-type"] = "text/plain" };

        InMemoryHeaderSet.Stamp(publisher, "x", "rk", "application/json")["content-type"].Should().Be("application/json");
        InMemoryHeaderSet.Stamp(publisher, "x", "rk", "")["content-type"].Should().Be("text/plain");
    }

    [Fact]
    public void Stamp_NonDictionaryInput_ProducesSameEntries()
    {
        IReadOnlyDictionary<string, string> readOnly = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["message-id"] = "m", ["BW-Exchange"] = "spoof", ["a"] = "1" });

        InMemoryHeaderSet set = InMemoryHeaderSet.Stamp(readOnly, "x", "rk", "");

        set.Select(kv => kv.Key).Should().Equal("message-id", "a", "BW-Exchange", "BW-RoutingKey");
    }

    [Fact]
    public void DictionaryContract_MissingKey_TryGetValueFalseAndIndexerThrows()
    {
        InMemoryHeaderSet set = InMemoryHeaderSet.Stamp(new Dictionary<string, string> { ["message-id"] = "m" }, "x", "rk", "");

        set.TryGetValue("absent", out _).Should().BeFalse();
        FluentActions.Invoking(() => set["absent"]).Should().Throw<KeyNotFoundException>();
        set.Count.Should().Be(3);
        set.Values.Should().Equal("m", "x", "rk");
    }

    [Fact]
    public void Stamp_NullArguments_Throw()
    {
        var h = new Dictionary<string, string>();
        FluentActions.Invoking(() => InMemoryHeaderSet.Stamp(null!, "x", "rk", "")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => InMemoryHeaderSet.Stamp(h, null!, "rk", "")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => InMemoryHeaderSet.Stamp(h, "x", null!, "")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => InMemoryHeaderSet.Stamp(h, "x", "rk", null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Stamp_TypicalPublishHeaders_AllocatesOnlyTheSetAndItsArray()
    {
        var publisher = new Dictionary<string, string>
        {
            ["message-id"] = "m-1", ["BW-MessageType"] = "T", ["x-a"] = "1",
            ["BW-Exchange"] = "spoof", ["BW-RoutingKey"] = "spoof",
        };

        for (int i = 0; i < WarmUpIterations; i++)
        {
            _ = InMemoryHeaderSet.Stamp(publisher, "orders", "rk", "application/json");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = InMemoryHeaderSet.Stamp(publisher, "orders", "rk", "application/json");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 32 B set object + 120 B six-entry array; a boxed enumerator (56 B), a List<T> (>= 32 B), or a
        // generated Guid string (96 B) would push this over budget — this test exists to catch exactly
        // that kind of regression, not merely to bound the total.
        allocated.Should().BeLessThanOrEqualTo(160);
    }

    [Fact]
    public void Stamp_MissingMessageId_AllocatesOneGuidStringPerPublish()
    {
        var publisher = new Dictionary<string, string>();

        for (int i = 0; i < WarmUpIterations; i++)
        {
            _ = InMemoryHeaderSet.Stamp(publisher, "orders", "rk", "");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = InMemoryHeaderSet.Stamp(publisher, "orders", "rk", "");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 32 B set object + 72 B three-entry array + 96 B generated Guid string; the fallback path
        // allocates exactly one Guid string per publish, never once per fan-out copy.
        allocated.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public void Keys_Values_DoNotExposeMutableStorage()
    {
        InMemoryHeaderSet set = InMemoryHeaderSet.Stamp(new Dictionary<string, string> { ["message-id"] = "m" }, "x", "rk", "");

        set.Keys.Should().NotBeAssignableTo<string[]>();
        set.Values.Should().NotBeAssignableTo<string[]>();
        set.Should().NotBeAssignableTo<ICollection<KeyValuePair<string, string>>>();
    }
}
