using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

public sealed class InMemoryOutboxStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

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
    public async Task ReleaseLockAsync_AfterGetPending_ReEnqueuesEntryWithoutRetainingCallerBuffer()
    {
        // Arrange — save one message and claim it (GetPendingAsync removes it from the pending queue).
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(timeProvider: clock);
        await store.SaveMessagesAsync([CreateMessage()]);

        IReadOnlyList<OutboxEntry> firstBatch = await store.GetPendingAsync(10);
        firstBatch.Should().HaveCount(1);
        long id = firstBatch[0].Id;

        // Act — a nack releases the lock, which for the in-memory store means re-enqueue, deferred
        // until the nack-deferral schedule says the entry is claimable again.
        IReadOnlySet<long> retained = await store.ReleaseLockAsync([id]);

        // Assert — the caller only ever held a copy with its own buffer, so nothing is retained and
        // the caller returns its buffer to the ArrayPool on every path.
        retained.Should().BeEmpty();

        // The released entry must be available again once the deferral has elapsed.
        clock.Advance(TimeSpan.FromSeconds(40));
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
    public async Task ReleaseLockAsync_NackedAndBarrierLists_ReEnqueuesBothWithoutRetainingCallerBuffers()
    {
        // Arrange — save two messages and claim both (GetPendingAsync removes them from the queue).
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(timeProvider: clock);
        await store.SaveMessagesAsync([CreateMessage(), CreateMessage()]);
        IReadOnlyList<OutboxEntry> firstBatch = await store.GetPendingAsync(10);
        firstBatch.Should().HaveCount(2);
        long nacked = firstBatch[0].Id;
        long barrierReleased = firstBatch[1].Id;

        // Act — one row rejected by the transport, one held back only by the ordering barrier.
        IReadOnlySet<long> retained = await store.ReleaseLockAsync([nacked], [barrierReleased]);

        // Assert — no caller buffer is retained, and only the barrier-released row is claimable
        // immediately: the nacked row is deferred by the nack-deferral schedule.
        retained.Should().BeEmpty();
        IReadOnlyList<OutboxEntry> secondBatch = await store.GetPendingAsync(10);
        secondBatch.Should().ContainSingle("the nacked row is still deferred").Which.Id.Should().Be(barrierReleased);

        // Once the deferral elapses, the nacked row is claimable too.
        clock.Advance(TimeSpan.FromSeconds(40));
        IReadOnlyList<OutboxEntry> thirdBatch = await store.GetPendingAsync(10);
        thirdBatch.Should().ContainSingle().Which.Id.Should().Be(nacked);
    }

    [Fact]
    public async Task ReleaseLockAsync_IdInBothLists_ReEnqueuesOnce()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(timeProvider: clock);
        await store.SaveMessagesAsync([CreateMessage()]);
        IReadOnlyList<OutboxEntry> firstBatch = await store.GetPendingAsync(10);
        long id = firstBatch.Should().ContainSingle().Which.Id;

        // Act — the same id passed in both lists must not be enqueued twice (it would be sent twice).
        // The nacked list is processed first, so the id is treated as a nack, not a barrier release.
        IReadOnlySet<long> retained = await store.ReleaseLockAsync([id], [id]);

        // Assert
        retained.Should().BeEmpty();
        clock.Advance(TimeSpan.FromSeconds(40));
        IReadOnlyList<OutboxEntry> secondBatch = await store.GetPendingAsync(10);
        secondBatch.Should().ContainSingle().Which.Id.Should().Be(id);
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

        // Assert — nothing re-enqueued.
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
    // Head-of-line per key + keyless passthrough
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
        // Default-off guard: None mode must not apply any head-of-line filtering.
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

    [Fact]
    public async Task GetPendingAsync_ClaimedEntry_CarriesItsOwnBufferCopy()
    {
        // Arrange
        await using var store = new InMemoryOutboxStore();
        await store.SaveMessagesAsync([CreateMessage()]);

        // Act
        OutboxEntry claimed = (await store.GetPendingAsync(10)).Should().ContainSingle().Which;
        OutboxEntry stored = store.FindEntry(claimed.Id)!;

        // Assert — same payload, different arrays: the caller owns and returns the copy, the store
        // keeps and returns its own buffer, so no array is ever returned to the pool twice.
        claimed.PooledBody.Should().NotBeSameAs(stored.PooledBody);
        claimed.PooledBody.AsSpan(0, claimed.BodyLength).ToArray()
            .Should().Equal(stored.PooledBody.AsSpan(0, stored.BodyLength).ToArray());
    }

    [Fact]
    public async Task ReleaseLockAsync_CallerReturnsClaimedBuffer_RedispatchStillCarriesOriginalPayload()
    {
        // Arrange — claim, then do what the dispatcher does on every path: return the claimed buffer.
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(timeProvider: clock);
        await store.SaveMessagesAsync([CreateMessage()]);
        OutboxEntry first = (await store.GetPendingAsync(10)).Should().ContainSingle().Which;
        byte[] original = first.PooledBody.AsSpan(0, first.BodyLength).ToArray();
        await store.ReleaseLockAsync([first.Id]);
        ArrayPool<byte>.Shared.Return(first.PooledBody);

        // Scribble over a same-sized rental, which the pool may hand back as that very array.
        byte[] scribble = ArrayPool<byte>.Shared.Rent(first.BodyLength);
        scribble.AsSpan().Fill(0xFF);

        // Act
        clock.Advance(TimeSpan.FromSeconds(40));
        OutboxEntry second = (await store.GetPendingAsync(10)).Should().ContainSingle().Which;

        // Assert — the re-dispatched entry is served from the store-owned buffer, untouched.
        second.PooledBody.AsSpan(0, second.BodyLength).ToArray().Should().Equal(original);
        ArrayPool<byte>.Shared.Return(scribble);
        ArrayPool<byte>.Shared.Return(second.PooledBody);
    }
}
