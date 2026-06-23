using AwesomeAssertions;
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
}
