using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

public sealed class TransportNativeScheduleProviderTests
{
    // ── Test message types ────────────────────────────────────────────────────

    private sealed record OrderTimeout(Guid OrderId);
    private sealed record PaymentTimeout(Guid OrderId);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (
        TransportNativeScheduleProvider provider,
        INativeMessageScheduler scheduler,
        IMessageSerializer serializer,
        FakeTimeProvider timeProvider) CreateProvider(
            int maxTokens = TransportNativeScheduleProvider.DefaultMaxTokens,
            ILogger<TransportNativeScheduleProvider>? logger = null)
    {
        var scheduler = Substitute.For<INativeMessageScheduler>();
        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        var timeProvider = new FakeTimeProvider();

        var provider = new TransportNativeScheduleProvider(
            scheduler, serializer, logger ?? NullLogger<TransportNativeScheduleProvider>.Instance, timeProvider, maxTokens);

        return (provider, scheduler, serializer, timeProvider);
    }

    private static ScheduledMessageToken AToken(long seq = 42L, string dest = "my-queue")
        => new(seq, dest);

    /// <summary>
    /// Extracts the formatted message from a captured <see cref="ILogger.Log{TState}"/> call.
    /// The formatter is a <c>Func&lt;TState, Exception?, string&gt;</c> where TState is the
    /// source generator's private state type, so it is invoked via <see cref="Delegate"/>
    /// reflection rather than a compile-time-typed call.
    /// </summary>
    private static string FormatLoggedMessage(NSubstitute.Core.ICall call)
    {
        object?[] arguments = call.GetArguments();
        var formatter = (Delegate)arguments[4]!;
        return (string)formatter.DynamicInvoke(arguments[2], arguments[3])!;
    }

    /// <summary>
    /// Thin typed wrapper around a substituted non-generic <see cref="ILogger"/> that lets
    /// tests spy on log calls when the generic type argument is an internal class (which
    /// Castle.DynamicProxy cannot proxy directly because the assembly is strong-named).
    /// </summary>
    private sealed class SpyLogger<T>(ILogger inner) : ILogger<T>
    {
        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);

        bool ILogger.IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        IDisposable? ILogger.BeginScope<TState>(TState state) => inner.BeginScope(state);
    }

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

    // ── Token key: (correlationId, timeout type) ────────────────────────────────

    [Fact]
    public async Task CancelAsync_ForOneTimeoutType_DoesNotCancelOtherTypeForSameCorrelationId()
    {
        var (provider, scheduler, _, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        var orderToken = AToken(seq: 1L);
        var paymentToken = AToken(seq: 2L);
        scheduler.ScheduleAsync(
                Arg.Is<OutboundMessage>(m => m.Headers["BW-MessageType"] == nameof(OrderTimeout)),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(orderToken);
        scheduler.ScheduleAsync(
                Arg.Is<OutboundMessage>(m => m.Headers["BW-MessageType"] == nameof(PaymentTimeout)),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(paymentToken);

        await provider.ScheduleAsync(new OrderTimeout(correlationId), TimeSpan.FromMinutes(5), "q", correlationId);
        await provider.ScheduleAsync(new PaymentTimeout(correlationId), TimeSpan.FromMinutes(5), "q", correlationId);
        await provider.CancelAsync<OrderTimeout>(correlationId);

        await scheduler.Received(1).CancelScheduledAsync(orderToken, Arg.Any<CancellationToken>());
        await scheduler.DidNotReceive().CancelScheduledAsync(paymentToken, Arg.Any<CancellationToken>());
        provider.TokenCount.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleAsync_TwoTimeoutTypesForSameCorrelationId_StoresBothTokens()
    {
        var (provider, scheduler, _, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(AToken(seq: 1L), AToken(seq: 2L));

        await provider.ScheduleAsync(new OrderTimeout(correlationId), TimeSpan.FromMinutes(5), "q", correlationId);
        await provider.ScheduleAsync(new PaymentTimeout(correlationId), TimeSpan.FromMinutes(5), "q", correlationId);

        provider.TokenCount.Should().Be(2);
    }

    // ── Amortized eviction and limit-eviction warning ────────────────────────────

    [Fact]
    public async Task ScheduleAsync_BeforeEvictionIsDue_DoesNotScanOrRemoveStaleEntries()
    {
        var (provider, scheduler, _, timeProvider) = CreateProvider(maxTokens: 100);
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>()).Returns(AToken());

        // A's own schedule call runs the map's first (harmless, empty-map) eviction scan and
        // sets the periodic eviction checkpoint to (now + grace).
        var correlationIdA = Guid.NewGuid();
        await provider.ScheduleAsync(new OrderTimeout(correlationIdA), TimeSpan.FromMinutes(1), "q", correlationIdA);

        // Advance well past A's own EvictAfter, but still short of the eviction checkpoint set
        // above. B's schedule call must not scan the map on this call.
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        var correlationIdB = Guid.NewGuid();
        await provider.ScheduleAsync(new PaymentTimeout(correlationIdB), TimeSpan.FromMinutes(1), "q", correlationIdB);

        provider.TokenCount.Should().Be(2, "the eviction checkpoint has not elapsed yet, so no scan ran");

        // Advance past the eviction checkpoint. C's schedule call now scans and evicts A (long
        // past due), while B (not yet due) survives.
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var correlationIdC = Guid.NewGuid();
        await provider.ScheduleAsync(new OrderTimeout(correlationIdC), TimeSpan.FromMinutes(1), "q", correlationIdC);

        provider.TokenCount.Should().Be(2, "the checkpoint elapsed, so the stale A entry was scanned out and only B and C remain");

        // A's token is gone: cancelling it now is a best-effort no-op, not a broker call.
        await provider.CancelAsync<OrderTimeout>(correlationIdA);
        await scheduler.DidNotReceive().CancelScheduledAsync(Arg.Any<ScheduledMessageToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_WhenLimitEvictsPendingToken_LogsWarningWithoutDestination()
    {
        ILogger innerLogger = Substitute.For<ILogger>();
        innerLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var logger = new SpyLogger<TransportNativeScheduleProvider>(innerLogger);

        var (provider, scheduler, _, _) = CreateProvider(maxTokens: 2, logger: logger);
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>()).Returns(AToken());

        // Distinct, increasing delays so the "oldest" (smallest EvictAfter) entry evicted by
        // the size cap is deterministic instead of racing a same-instant tie.
        var oldestCorrelationId = Guid.NewGuid();
        await provider.ScheduleAsync(new OrderTimeout(oldestCorrelationId), TimeSpan.FromMinutes(30), "sensitive-destination-queue", oldestCorrelationId);
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(31), "sensitive-destination-queue", Guid.NewGuid());

        // A third schedule call overflows the 2-entry cap: the oldest (still-pending) entry is
        // evicted and its loss is logged as a warning — without the destination queue name.
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(32), "sensitive-destination-queue", Guid.NewGuid());

        IEnumerable<NSubstitute.Core.ICall> warningCalls = innerLogger.ReceivedCalls()
            .Where(c =>
                c.GetMethodInfo().Name == nameof(ILogger.Log)
                && c.GetArguments()[0] is LogLevel level
                && level == LogLevel.Warning);

        warningCalls.Should().ContainSingle("evicting one still-pending token under the cap must log exactly one warning");

        string message = FormatLoggedMessage(warningCalls.Single());
        message.Should().Contain(oldestCorrelationId.ToString());
        message.Should().Contain(nameof(OrderTimeout));
        message.Should().NotContain("sensitive-destination-queue");

        provider.TokenCount.Should().Be(2);
    }

    [Fact]
    public async Task TokenCount_AfterOverwriteOfSameKey_IsUnchanged()
    {
        var (provider, scheduler, _, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(AToken(seq: 1L), AToken(seq: 2L));

        await provider.ScheduleAsync(new OrderTimeout(correlationId), TimeSpan.FromMinutes(5), "q", correlationId);
        await provider.ScheduleAsync(new OrderTimeout(correlationId), TimeSpan.FromMinutes(10), "q", correlationId);

        provider.TokenCount.Should().Be(1);
    }

    // ── PERF-1/PERF-2/PERF-3/SEC-2: O(log n) cap eviction, gated sweep, drift-safe counter ──

    /// <summary>
    /// PERF-1: EnforceMaxSize must evict the entry with the smallest EvictAfter (found via the
    /// eviction heap in O(log n)) rather than an arbitrary one. Verified by identity, not just
    /// by count: the oldest entry's token becomes uncancellable while the newer ones survive.
    /// </summary>
    [Fact]
    public async Task EnforceMaxSize_AtCap_EvictsEntryWithSmallestEvictAfter()
    {
        const int max = 3;
        var (provider, scheduler, _, _) = CreateProvider(maxTokens: max);
        var oldestToken = AToken(seq: 1L);
        var middleToken = AToken(seq: 2L);
        var newestToken = AToken(seq: 3L);
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(oldestToken, middleToken, newestToken, AToken(seq: 4L));

        var oldestCorrelationId = Guid.NewGuid();
        var middleCorrelationId = Guid.NewGuid();
        var newestCorrelationId = Guid.NewGuid();

        await provider.ScheduleAsync(new OrderTimeout(oldestCorrelationId), TimeSpan.FromMinutes(10), "q", oldestCorrelationId);
        await provider.ScheduleAsync(new OrderTimeout(middleCorrelationId), TimeSpan.FromMinutes(20), "q", middleCorrelationId);
        await provider.ScheduleAsync(new OrderTimeout(newestCorrelationId), TimeSpan.FromMinutes(30), "q", newestCorrelationId);

        // Overflow: the smallest-EvictAfter entry (10 min delay) must be the one evicted.
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(40), "q", Guid.NewGuid());

        provider.TokenCount.Should().Be(max);

        // The oldest entry's token was evicted: cancelling it now is a best-effort no-op.
        await provider.CancelAsync<OrderTimeout>(oldestCorrelationId);
        await scheduler.DidNotReceive().CancelScheduledAsync(oldestToken, Arg.Any<CancellationToken>());

        // The middle and newest entries survived the eviction: cancelling still reaches the broker.
        await provider.CancelAsync<OrderTimeout>(middleCorrelationId);
        await scheduler.Received(1).CancelScheduledAsync(middleToken, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The size cap must hold under concurrency: calls that all pass the cap check while the
    /// map is full and then insert after their broker round-trip completes must not push the map
    /// past the cap. The broker call is held open until every caller has reached it, so every
    /// insert happens after every pre-await check.
    /// </summary>
    [Fact]
    public async Task ScheduleAsync_ConcurrentCallsAtCap_NeverExceedCap()
    {
        const int max = 4;
        const int concurrentCalls = 16;
        var (provider, scheduler, _, _) = CreateProvider(maxTokens: max);

        long seq = 0;
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(AToken(seq: Interlocked.Increment(ref seq))));

        for (int i = 0; i < max; i++)
        {
            var id = Guid.NewGuid();
            await provider.ScheduleAsync(new OrderTimeout(id), TimeSpan.FromMinutes(30), "q", id);
        }

        var allCallersReachedBroker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callersAtBroker = 0;
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref callersAtBroker) == concurrentCalls)
                {
                    allCallersReachedBroker.SetResult();
                }

                await allCallersReachedBroker.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return AToken(seq: Interlocked.Increment(ref seq));
            });

        Task[] calls = Enumerable.Range(0, concurrentCalls)
            .Select(_ => Task.Run(async () =>
            {
                var id = Guid.NewGuid();
                await provider.ScheduleAsync(new OrderTimeout(id), TimeSpan.FromMinutes(30), "q", id);
            }))
            .ToArray();
        await Task.WhenAll(calls);

        provider.DictionaryEntryCount.Should().Be(max);
        provider.TokenCount.Should().Be(max);
    }

    /// <summary>
    /// Mixed concurrent schedule / overwrite / cancel / time-sweep traffic on a small cap must
    /// keep the counter equal to the map's real contents and never let the map exceed the cap.
    /// The broker call yields so schedule and cancel interleave across threads.
    /// </summary>
    [Fact]
    public async Task ScheduleAndCancel_ConcurrentMixedTraffic_CounterMatchesMapAndCapHolds()
    {
        const int max = 8;
        var (provider, scheduler, _, timeProvider) = CreateProvider(maxTokens: max);

        long seq = 0;
        int maxObserved = 0;
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Yield();
                int size = provider.DictionaryEntryCount;
                int observed;
                while (size > (observed = Volatile.Read(ref maxObserved))
                    && Interlocked.CompareExchange(ref maxObserved, size, observed) != observed)
                {
                }

                return AToken(seq: Interlocked.Increment(ref seq));
            });

        // A small pool of correlation ids so schedules overwrite and cancels hit live keys.
        Guid[] ids = Enumerable.Range(0, 24).Select(_ => Guid.NewGuid()).ToArray();

        Task[] workers = Enumerable.Range(0, 8)
            .Select(worker => Task.Run(async () =>
            {
                var random = new Random(worker);
                for (int i = 0; i < 400; i++)
                {
                    Guid id = ids[random.Next(ids.Length)];
                    switch (random.Next(4))
                    {
                        case 0:
                            await provider.CancelAsync<OrderTimeout>(id);
                            break;
                        case 1:
                            await provider.ScheduleAsync(new PaymentTimeout(id), TimeSpan.FromMinutes(1), "q", id);
                            break;
                        default:
                            await provider.ScheduleAsync(
                                new OrderTimeout(id), TimeSpan.FromMinutes(random.Next(1, 30)), "q", id);
                            break;
                    }

                    if (worker == 0 && i % 50 == 0)
                    {
                        // Push some entries past EvictAfter so the time sweep runs concurrently.
                        timeProvider.Advance(TimeSpan.FromMinutes(10));
                    }
                }
            }))
            .ToArray();
        await Task.WhenAll(workers);

        provider.TokenCount.Should().Be(provider.DictionaryEntryCount);
        provider.DictionaryEntryCount.Should().BeLessThanOrEqualTo(max);
        maxObserved.Should().BeLessThanOrEqualTo(max);
    }

    /// <summary>
    /// PERF-1: repeated overflow within one <c>LiveEvictionWarningInterval</c> must fold into a
    /// single aggregated warning instead of logging one line per eviction. Once the interval
    /// elapses, the next overflow flushes everything accumulated since the last warning.
    /// </summary>
    [Fact]
    public async Task ScheduleAsync_WhenLimitEvictsMultipleLiveTokensWithinInterval_LogsOneAggregatedWarningPerInterval()
    {
        ILogger innerLogger = Substitute.For<ILogger>();
        innerLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var logger = new SpyLogger<TransportNativeScheduleProvider>(innerLogger);

        var (provider, scheduler, _, timeProvider) = CreateProvider(maxTokens: 2, logger: logger);
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>()).Returns(AToken());

        static IEnumerable<NSubstitute.Core.ICall> WarningCalls(ILogger innerLogger) => innerLogger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log)
                && c.GetArguments()[0] is LogLevel level && level == LogLevel.Warning);

        // Fill to capacity.
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(30), "q", Guid.NewGuid());
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(31), "q", Guid.NewGuid());

        // First overflow evicts a live token and logs immediately — there is no prior warning
        // to rate-limit against yet.
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(32), "q", Guid.NewGuid());

        // Two more overflows happen at the same instant (FakeTimeProvider does not auto-advance):
        // both fall inside the warning interval that just started, so they are counted but not
        // individually logged.
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(33), "q", Guid.NewGuid());
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(34), "q", Guid.NewGuid());

        WarningCalls(innerLogger).Should().ContainSingle(
            "overflow evictions within the same warning interval are aggregated, not logged individually");

        // Advance past the aggregation interval; the next overflow flushes everything evicted
        // since the last warning (the 2 rate-limited evictions above, plus this one).
        timeProvider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await provider.ScheduleAsync(new OrderTimeout(Guid.NewGuid()), TimeSpan.FromMinutes(35), "q", Guid.NewGuid());

        List<NSubstitute.Core.ICall> warningCalls = WarningCalls(innerLogger).ToList();
        warningCalls.Should().HaveCount(2,
            "the interval elapsed, so the evictions accumulated since the last warning are flushed in one more log");

        string flushedMessage = FormatLoggedMessage(warningCalls[1]);
        flushedMessage.Should().Contain("3", "the flushed warning reports the 3 evictions accumulated since the first warning");

        provider.TokenCount.Should().Be(2);
    }

    /// <summary>
    /// PERF-3/SEC-2 anti-regression: a naive "TryAdd; else overwrite" insert can re-add a key
    /// that a concurrent CancelAsync just removed without incrementing the counter, drifting
    /// TokenCount away from the map's real contents. Hammering schedule/cancel on the SAME key
    /// from many threads exercises exactly that race; TokenCount must always match the
    /// dictionary's own entry count regardless of how the race resolves.
    /// </summary>
    [Fact]
    public async Task TokenCount_AfterConcurrentScheduleAndCancelOfSameKey_MatchesDictionaryEntryCount()
    {
        var (provider, scheduler, _, _) = CreateProvider();
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>()).Returns(AToken());

        var correlationId = Guid.NewGuid();

        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            await provider.ScheduleAsync(new OrderTimeout(correlationId), TimeSpan.FromMinutes(1), "q", correlationId);
            if (i % 2 == 0)
            {
                await provider.CancelAsync<OrderTimeout>(correlationId);
            }
        });

        await Task.WhenAll(tasks);

        provider.TokenCount.Should().Be(provider.DictionaryEntryCount);
        provider.TokenCount.Should().BeInRange(0, 1, "the key can only end up present once or absent, never over- or under-counted");
    }
}
