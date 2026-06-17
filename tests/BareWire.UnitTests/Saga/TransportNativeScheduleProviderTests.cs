using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

public sealed class TransportNativeScheduleProviderTests
{
    // ── Test message type ─────────────────────────────────────────────────────

    private sealed record OrderTimeout(Guid OrderId);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (
        TransportNativeScheduleProvider provider,
        INativeMessageScheduler scheduler,
        IMessageSerializer serializer,
        FakeTimeProvider timeProvider) CreateProvider(int maxTokens = TransportNativeScheduleProvider.DefaultMaxTokens)
    {
        var scheduler = Substitute.For<INativeMessageScheduler>();
        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        var timeProvider = new FakeTimeProvider();
        var logger = NullLogger<TransportNativeScheduleProvider>.Instance;

        var provider = new TransportNativeScheduleProvider(
            scheduler, serializer, logger, timeProvider, maxTokens);

        return (provider, scheduler, serializer, timeProvider);
    }

    private static ScheduledMessageToken AToken(long seq = 42L, string dest = "my-queue")
        => new(seq, dest);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleAsync_DelegatesToNativeScheduler_WithComputedEnqueueTime()
    {
        var (provider, scheduler, _, timeProvider) = CreateProvider();
        var correlationId = Guid.NewGuid();
        var message = new OrderTimeout(correlationId);
        var delay = TimeSpan.FromMinutes(10);

        var expectedEnqueueAt = timeProvider.GetUtcNow() + delay;
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(AToken());

        await provider.ScheduleAsync(message, delay, "my-queue", correlationId);

        await scheduler.Received(1).ScheduleAsync(
            Arg.Is<OutboundMessage>(m => m.RoutingKey == "my-queue"),
            Arg.Is<DateTimeOffset>(t => t == expectedEnqueueAt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_StoresTokenKeyedOnCorrelationId()
    {
        var (provider, scheduler, _, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        var token = AToken(seq: 99L, dest: "my-queue");
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(token);

        await provider.ScheduleAsync(new OrderTimeout(correlationId), TimeSpan.FromMinutes(5), "my-queue", correlationId);

        provider.TokenCount.Should().Be(1);
    }

    /// <summary>
    /// GAP-1 anti-regression: schedule and cancel MUST use the SAME correlationId key.
    /// If the provider keys on a different value (e.g. Guid.NewGuid()), TryRemove returns
    /// false and CancelScheduledAsync is never called — this test catches that.
    /// </summary>
    [Fact]
    public async Task ScheduleThenCancel_BySameCorrelationId_InvokesNativeCancelWithStoredToken()
    {
        var (provider, scheduler, _, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        var storedToken = AToken(seq: 77L, dest: "saga-queue");

        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(storedToken);

        // Schedule using the saga correlationId
        await provider.ScheduleAsync(new OrderTimeout(correlationId), TimeSpan.FromMinutes(30), "saga-queue", correlationId);

        // Cancel using the EXACT SAME correlationId
        await provider.CancelAsync<OrderTimeout>(correlationId);

        // Assert CancelScheduledAsync was called with the token that was stored at schedule time
        await scheduler.Received(1).CancelScheduledAsync(
            Arg.Is<ScheduledMessageToken>(t => t == storedToken),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAsync_WithUnknownCorrelationId_IsBestEffortNoOp()
    {
        var (provider, scheduler, _, _) = CreateProvider();
        var unknownCorrelationId = Guid.NewGuid();

        // No exception, no CancelScheduledAsync call
        Func<Task> act = () => provider.CancelAsync<OrderTimeout>(unknownCorrelationId);

        await act.Should().NotThrowAsync();
        await scheduler.DidNotReceive().CancelScheduledAsync(Arg.Any<ScheduledMessageToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_EvictsEntriesPastEnqueueTime()
    {
        var (provider, scheduler, _, timeProvider) = CreateProvider();
        var correlationId1 = Guid.NewGuid();
        var token1 = AToken(seq: 1L);

        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(token1, AToken(seq: 2L));

        var delay = TimeSpan.FromMinutes(5);

        // Schedule first entry
        await provider.ScheduleAsync(new OrderTimeout(correlationId1), delay, "q", correlationId1);
        provider.TokenCount.Should().Be(1);

        // Advance time past EnqueueAt + EvictionGrace (5 min delay + 5 min grace + 1 sec)
        var advanceBy = delay + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        timeProvider.Advance(advanceBy);

        // Schedule a second entry — triggers eviction of the stale first entry
        var correlationId2 = Guid.NewGuid();
        await provider.ScheduleAsync(new OrderTimeout(correlationId2), TimeSpan.FromMinutes(1), "q", correlationId2);

        // The first (stale) entry was evicted; only the second remains
        provider.TokenCount.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleAsync_WhenAtMaxTokens_EvictsOldestEntry()
    {
        const int max = 3;
        var (provider, scheduler, _, _) = CreateProvider(maxTokens: max);

        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(AToken());

        // Fill to capacity
        for (int i = 0; i < max; i++)
        {
            await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(i + 1), "q", Guid.NewGuid());
        }

        provider.TokenCount.Should().Be(max);

        // One more — should evict the oldest (smallest EvictAfter) and stay at max
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(max + 1), "q", Guid.NewGuid());

        provider.TokenCount.Should().Be(max);
    }

    [Fact]
    public async Task ScheduleAsync_SetsCorrelationIdHeaderToSagaCorrelationId()
    {
        var (provider, scheduler, _, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(AToken());

        await provider.ScheduleAsync(new OrderTimeout(correlationId), TimeSpan.FromMinutes(1), "q", correlationId);

        await scheduler.Received(1).ScheduleAsync(
            Arg.Is<OutboundMessage>(m =>
                m.Headers.ContainsKey("correlation-id") &&
                m.Headers["correlation-id"] == correlationId.ToString()),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// PERF-3 edge case: when an entry has EvictAfter == DateTimeOffset.MaxValue (produced by
    /// an extreme delay that would overflow), EnforceMaxSize must still evict it so the cap
    /// is enforced. Uses a large but non-overflowing delay (10 years) to produce a near-MaxValue
    /// EvictAfter, then inserts one more entry and asserts TokenCount stays at maxTokens.
    /// </summary>
    [Fact]
    public async Task EnforceMaxSize_WhenEntryHasMaxValueEvictAfter_StillEvictsAndEnforcesCap()
    {
        const int max = 1;
        var (provider, scheduler, _, _) = CreateProvider(maxTokens: max);

        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(AToken());

        // Use a very large but non-overflowing delay to produce an EvictAfter near DateTimeOffset.MaxValue.
        // TimeSpan.FromDays(3650) ≈ 10 years — safe from arithmetic overflow with DateTimeOffset.UtcNow.
        var largeDelay = TimeSpan.FromDays(3650);
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), largeDelay, "q", Guid.NewGuid());

        provider.TokenCount.Should().Be(max);

        // Insert another entry — the near-MaxValue entry must be evicted to keep the cap
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(1), "q", Guid.NewGuid());

        provider.TokenCount.Should().Be(max);
    }
}
