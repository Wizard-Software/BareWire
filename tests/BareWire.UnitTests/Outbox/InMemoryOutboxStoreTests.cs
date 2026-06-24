using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Xunit;

namespace BareWire.UnitTests.Outbox;

public sealed class InMemoryOutboxStoreTests
{
    private static OutboundMessage CreateMessage(string routingKey = "test.routing.key")
        => new(
            routingKey: routingKey,
            headers: new Dictionary<string, string>(),
            body: "test-body"u8.ToArray(),
            contentType: "application/json");

    private static OutboundMessage CreateMessageWithKey(string orderingKey, string routingKey = "test.routing.key")
        => new(
            routingKey: routingKey,
            headers: new Dictionary<string, string> { ["x-ordering-key"] = orderingKey },
            body: "test-body"u8.ToArray(),
            contentType: "application/json");

    private static OutboxOptions PerKeyOptions(string headerName = "x-ordering-key") =>
        new OutboxOptions { OrderingMode = OrderingMode.PerKey, OrderingKeyHeaderName = headerName };

    [Fact]
    public async Task ReleaseLockAsync_AfterGetPending_ReEnqueuesEntryAndRetainsBuffer()
    {
        // Arrange — save one message and claim it (GetPendingAsync removes it from the pending queue).
        await using var store = new InMemoryOutboxStore();
        await store.SaveMessagesAsync([CreateMessage()]);

        IReadOnlyList<OutboxEntry> firstBatch = await store.GetPendingAsync(10);
        firstBatch.Should().HaveCount(1);
        long id = firstBatch[0].Id;

        // Act — a nack releases the lock, which for the in-memory store means re-enqueue.
        IReadOnlySet<long> retained = await store.ReleaseLockAsync([id]);

        // Assert — the re-enqueued entry still references its pooled buffer, so the store reports it
        // as retained (the dispatcher must NOT return that buffer to the ArrayPool — R-5/GAP-1).
        retained.Should().BeEquivalentTo([id]);

        // The released entry must be available again on the next poll.
        IReadOnlyList<OutboxEntry> secondBatch = await store.GetPendingAsync(10);
        secondBatch.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task ReleaseLockAsync_EmptyList_IsNoOpAndRetainsNothing()
    {
        await using var store = new InMemoryOutboxStore();

        IReadOnlySet<long> retained = await store.ReleaseLockAsync([]);

        retained.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseLockAsync_DeliveredEntry_IsNotReEnqueued()
    {
        // Arrange — save, claim, and mark the entry delivered.
        await using var store = new InMemoryOutboxStore();
        await store.SaveMessagesAsync([CreateMessage()]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);
        long id = batch[0].Id;
        await store.MarkDeliveredAsync([id]);

        // Act — releasing a delivered id must be an idempotent no-op.
        IReadOnlySet<long> retained = await store.ReleaseLockAsync([id]);

        // Assert — nothing retained, nothing re-enqueued.
        retained.Should().BeEmpty("a delivered entry must not be re-enqueued");
        IReadOnlyList<OutboxEntry> afterRelease = await store.GetPendingAsync(10);
        afterRelease.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseLockAsync_UnknownId_IsNoOp()
    {
        await using var store = new InMemoryOutboxStore();

        IReadOnlySet<long> retained = await store.ReleaseLockAsync([999L]);

        retained.Should().BeEmpty("an id not present in the store must be skipped");
    }

    // -------------------------------------------------------------------------
    // U7 — head-of-line per key + keyless passthrough (R7.7.5)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPendingAsync_PerKey_BlocksNewerRowsUntilHeadIsDelivered()
    {
        // Arrange — two rows for key "K": save in order so Id(1) < Id(2).
        await using var store = new InMemoryOutboxStore(PerKeyOptions());

        await store.SaveMessagesAsync([
            CreateMessageWithKey("K"),  // head: lowest Id
            CreateMessageWithKey("K")   // sibling: blocked until head is delivered
        ]);

        // Act — first poll: should return only the head (lower Id).
        IReadOnlyList<OutboxEntry> firstBatch = await store.GetPendingAsync(10);

        // Assert — only one row returned, and it is the head.
        firstBatch.Should().ContainSingle("only the head of key K is claimable");
        long headId = firstBatch[0].Id;

        // Act — second poll before delivering the head: sibling must still be blocked.
        IReadOnlyList<OutboxEntry> secondBatch = await store.GetPendingAsync(10);
        secondBatch.Should().BeEmpty("the sibling must be blocked while the head is still undelivered");

        // Act — mark the head delivered, then re-poll.
        await store.MarkDeliveredAsync([headId]);
        IReadOnlyList<OutboxEntry> thirdBatch = await store.GetPendingAsync(10);

        // Assert — sibling is now the new head and must be returned.
        thirdBatch.Should().ContainSingle("sibling becomes claimable once the head is delivered");
        thirdBatch[0].Id.Should().NotBe(headId, "the returned row must be the formerly blocked sibling");
    }

    [Fact]
    public async Task GetPendingAsync_PerKey_KeylessRowsPassThroughUnblocked()
    {
        // Arrange — one keyed head row (blocked) and one keyless row (must not be blocked).
        await using var store = new InMemoryOutboxStore(PerKeyOptions());

        await store.SaveMessagesAsync([
            CreateMessageWithKey("K"),          // head row for key K
            CreateMessageWithKey("K"),          // sibling — blocked until head delivered
            CreateMessage("keyless.route")      // keyless — must pass through immediately
        ]);

        // Claim the head via first poll.
        IReadOnlyList<OutboxEntry> firstBatch = await store.GetPendingAsync(10);

        // The first poll must return the head of K plus the keyless row (both unblocked).
        // The keyless row and the head of K are both eligible; the sibling is blocked.
        firstBatch.Should().HaveCount(2, "head of K and the keyless row are both eligible");
        firstBatch.Should().Contain(e => e.OrderingKey == "K", "head of key K must be included");
        firstBatch.Should().Contain(e => e.OrderingKey == null, "keyless row must pass through");

        // Second poll — head claimed, sibling still blocked, no keyless left.
        IReadOnlyList<OutboxEntry> secondBatch = await store.GetPendingAsync(10);
        secondBatch.Should().BeEmpty(
            "sibling is still blocked (head not yet delivered) and there are no more keyless rows");
    }

    [Fact]
    public async Task GetPendingAsync_PerKey_MultipleKeysClaimedIndependently()
    {
        // Arrange — head rows for two independent keys A and B.
        await using var store = new InMemoryOutboxStore(PerKeyOptions());

        await store.SaveMessagesAsync([
            CreateMessageWithKey("A"),  // head of A
            CreateMessageWithKey("B"),  // head of B
            CreateMessageWithKey("A"),  // sibling of A — blocked
            CreateMessageWithKey("B"),  // sibling of B — blocked
        ]);

        // Act — poll should return heads of both A and B.
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert — exactly the two heads, one per key.
        batch.Should().HaveCount(2, "one head per key must be claimable concurrently");
        batch.Should().Contain(e => e.OrderingKey == "A", "head of key A must be included");
        batch.Should().Contain(e => e.OrderingKey == "B", "head of key B must be included");
    }

    [Fact]
    public async Task GetPendingAsync_None_ReturnsAllPendingWithoutGrouping()
    {
        // Default-OFF guard (§2.1): None mode must not apply any head-of-line filtering.
        await using var store = new InMemoryOutboxStore(); // default options = None

        await store.SaveMessagesAsync([
            CreateMessageWithKey("K"),  // would be head in PerKey
            CreateMessageWithKey("K"),  // would be blocked in PerKey
            CreateMessage()             // keyless
        ]);

        // Act
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert — all three rows returned without any head-of-line filtering.
        batch.Should().HaveCount(3,
            "None mode must return all pending rows without per-key grouping");
    }
}
