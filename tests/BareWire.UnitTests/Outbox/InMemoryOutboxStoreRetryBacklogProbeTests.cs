using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

// InMemoryOutboxStore.GetOldestDueRetryAsync — IOutboxRetryBacklogProbe over the in-memory store.
public sealed class InMemoryOutboxStoreRetryBacklogProbeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly OutboxOptions Options = new()
        { PollingInterval = TimeSpan.FromSeconds(1), OutboxLockTimeout = TimeSpan.FromSeconds(30) };

    private sealed class ZeroJitter : IOutboxJitterSource { public double NextDouble() => 0.0; }

    private static OutboundMessage CreateMessage()
        => new(
            routingKey: "test.routing.key",
            headers: new Dictionary<string, string>(),
            body: "test-body"u8.ToArray(),
            contentType: "application/json");

    private static async Task<IReadOnlyList<OutboxEntry>> SaveAndClaimAsync(InMemoryOutboxStore store, int count)
    {
        var messages = new List<OutboundMessage>(count);
        for (int i = 0; i < count; i++)
        {
            messages.Add(CreateMessage());
        }

        await store.SaveMessagesAsync(messages);
        return await store.GetPendingAsync(count);
    }

    [Fact]
    public async Task GetOldestDueRetryAsync_NoNackedRows_ReturnsNull()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        await SaveAndClaimAsync(store, 1);

        DateTimeOffset? result = await store.GetOldestDueRetryAsync(clock.GetUtcNow());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOldestDueRetryAsync_NackedRowDeferralNotElapsed_ReturnsNull()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        OutboxEntry entry = (await SaveAndClaimAsync(store, 1))[0];

        await store.ReleaseLockAsync([entry.Id], []);

        DateTimeOffset? result = await store.GetOldestDueRetryAsync(clock.GetUtcNow());

        result.Should().BeNull("the deferral has not elapsed yet");
    }

    [Fact]
    public async Task GetOldestDueRetryAsync_TwoDueRetries_ReturnsEarliestNotBefore()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        IReadOnlyList<OutboxEntry> claimed = await SaveAndClaimAsync(store, 2);
        long idA = claimed[0].Id;
        long idB = claimed[1].Id;

        await store.ReleaseLockAsync([idA], []);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await store.ReleaseLockAsync([idB], []);

        // Both deferrals are relative to PollingInterval (1s); advancing well past OutboxLockTimeout
        // guarantees both are strictly due regardless of jitter bucket.
        clock.Advance(Options.OutboxLockTimeout + Options.OutboxLockTimeout);
        DateTimeOffset now = clock.GetUtcNow();

        DateTimeOffset? result = await store.GetOldestDueRetryAsync(now);

        DateTimeOffset? notBeforeA = store.FindEntry(idA)!.NotBefore;
        DateTimeOffset? notBeforeB = store.FindEntry(idB)!.NotBefore;
        DateTimeOffset expected = notBeforeA < notBeforeB ? notBeforeA!.Value : notBeforeB!.Value;

        result.Should().Be(expected);
        result!.Value.Should().BeBefore(now);
    }

    [Fact]
    public async Task GetOldestDueRetryAsync_BarrierReleasedRow_IsNotCounted()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        OutboxEntry entry = (await SaveAndClaimAsync(store, 1))[0];

        await store.ReleaseLockAsync([], [entry.Id]);
        clock.Advance(Options.OutboxLockTimeout + Options.OutboxLockTimeout);

        DateTimeOffset? result = await store.GetOldestDueRetryAsync(clock.GetUtcNow());

        result.Should().BeNull("a barrier release clears NotBefore — it is not a retry");
    }
}
