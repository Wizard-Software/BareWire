// NSubstitute's Returns() for ValueTask-returning mocks triggers CA2012 as a false positive.
// The ValueTask is consumed internally by NSubstitute and never double-consumed.
#pragma warning disable CA2012

using System.Collections.Frozen;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace BareWire.UnitTests.Outbox;

public sealed class OutboxDispatcherTests
{
    private readonly IOutboxStore _store;
    private readonly ITransportAdapter _adapter;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public OutboxDispatcherTests()
    {
        _store = Substitute.For<IOutboxStore>();
        _adapter = Substitute.For<ITransportAdapter>();
        _logger = Substitute.For<ILogger<OutboxDispatcher>>();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IOutboxStore)).Returns(_store);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(scope);

        // Default: adapter returns empty results for every send.
        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<SendResult>>(Array.Empty<SendResult>()));

        // Default: ReleaseLockAsync retains no buffers (mirrors EF Core, which rents fresh per-cycle
        // buffers). Tests that assert on nacked behaviour override the capture but keep this shape.
        _store
            .ReleaseLockAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty));
    }

    private OutboxDispatcher CreateSut(
        TimeSpan? pollingInterval = null,
        int batchSize = 100,
        IHostApplicationLifetime? lifetime = null)
    {
        var options = new OutboxOptions
        {
            PollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(10),
            DispatchBatchSize = batchSize
        };
        return new OutboxDispatcher(_scopeFactory, _adapter, options, _logger, lifetime ?? StartedLifetime());
    }

    private OutboxDispatcher CreateSutWithOptions(OutboxOptions options)
        => new OutboxDispatcher(_scopeFactory, _adapter, options, _logger, StartedLifetime());

    // A lifetime whose ApplicationStarted has already fired, so the dispatcher's loop starts
    // immediately on StartAsync — preserving the behaviour the timing tests rely on.
    private static IHostApplicationLifetime StartedLifetime()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var started = new CancellationTokenSource();
        started.Cancel();
        lifetime.ApplicationStarted.Returns(started.Token);
        return lifetime;
    }

    // PooledBody must be rented from ArrayPool.Shared (as EfCoreOutboxStore.GetPendingAsync does):
    // the dispatcher returns it to the shared pool in its finally block, and the pool rejects a plain
    // (non-rented) array on Return with an ArgumentException. BodyLength is the logical length; the
    // rented buffer may be larger.
    private static OutboxEntry CreateEntry(long id, string routingKey = "test.routing.key")
    {
        ReadOnlySpan<byte> body = "test-body"u8;
        byte[] pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(body.Length);
        body.CopyTo(pooled);
        return new OutboxEntry
        {
            Id = id,
            RoutingKey = routingKey,
            Headers = new Dictionary<string, string>(),
            PooledBody = pooled,
            BodyLength = body.Length,
            ContentType = "application/json",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = OutboxEntryStatus.Pending
        };
    }

    private static OutboxEntry CreateKeyedEntry(long id, string? orderingKey, string routingKey = "test.routing.key")
    {
        ReadOnlySpan<byte> body = "test-body"u8;
        byte[] pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(body.Length);
        body.CopyTo(pooled);
        return new OutboxEntry
        {
            Id = id,
            RoutingKey = routingKey,
            Headers = new Dictionary<string, string>(),
            PooledBody = pooled,
            BodyLength = body.Length,
            ContentType = "application/json",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = OutboxEntryStatus.Pending,
            OrderingKey = orderingKey
        };
    }

    [Fact]
    public async Task StartAsync_PollsAndDispatchesPendingMessages()
    {
        // Arrange
        var entries = new List<OutboxEntry> { CreateEntry(1, "orders.created"), CreateEntry(2, "orders.updated") };
        IReadOnlyList<OutboundMessage>? capturedMessages = null;
        IReadOnlyList<long>? capturedIds = null;
        bool firstGetPending = true;

        // Return entries on the first call, then empty list to stop further dispatch.
        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (firstGetPending)
                {
                    firstGetPending = false;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(entries);
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedMessages = ci.Arg<IReadOnlyList<OutboundMessage>>();
                // Return confirmed results for each message so MarkDeliveredAsync is called.
                var results = capturedMessages.Select((_, i) =>
                    new SendResult(IsConfirmed: true, DeliveryTag: (ulong)i)).ToArray();
                return Task.FromResult<IReadOnlyList<SendResult>>(results);
            });

        _store
            .MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedIds = ci.Arg<IReadOnlyList<long>>();
                return ValueTask.CompletedTask;
            });

        await using var sut = CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50); // Allow at least one poll tick to complete.
        await sut.StopAsync(CancellationToken.None);

        // Assert
        await _adapter.Received().SendBatchAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());

        capturedMessages.Should().NotBeNull();
        capturedMessages!.Count.Should().Be(2);
        capturedMessages[0].RoutingKey.Should().Be("orders.created");
        capturedMessages[1].RoutingKey.Should().Be("orders.updated");

        await _store.Received().MarkDeliveredAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());

        capturedIds.Should().NotBeNull();
        capturedIds!.Should().BeEquivalentTo([1L, 2L]);
    }

    [Fact]
    public async Task StartAsync_EmptyStore_NoDispatch()
    {
        // Arrange
        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>()));

        await using var sut = CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50); // Allow several poll ticks to run with empty store.
        await sut.StopAsync(CancellationToken.None);

        // Assert — adapter must never be called when there are no pending messages.
        await _adapter.DidNotReceive().SendBatchAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_BatchSizeRespected()
    {
        // Arrange — dispatcher must request exactly the configured batch size.
        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>()));

        const int configuredBatchSize = 42;
        await using var sut = CreateSut(batchSize: configuredBatchSize);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50); // Allow at least one poll tick.
        await sut.StopAsync(CancellationToken.None);

        // Assert — store must have been queried with the exact configured batch size.
        await _store.Received().GetPendingAsync(
            configuredBatchSize,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_AlreadyDelivered_Skipped()
    {
        // Arrange — GetPendingAsync returns only Pending entries (store filters Delivered).
        // This test verifies correct integration: only what the store returns is dispatched.
        var pendingEntry = CreateEntry(10, "events.something");
        bool firstCall = true;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(new[] { pendingEntry });
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var msgs = ci.Arg<IReadOnlyList<OutboundMessage>>();
                var results = msgs.Select((_, i) =>
                    new SendResult(IsConfirmed: true, DeliveryTag: (ulong)i)).ToArray();
                return Task.FromResult<IReadOnlyList<SendResult>>(results);
            });

        IReadOnlyList<long>? markedIds = null;
        _store
            .MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                markedIds = ci.Arg<IReadOnlyList<long>>();
                return ValueTask.CompletedTask;
            });

        await using var sut = CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // Assert — only the one pending entry was sent and marked.
        await _adapter.Received(1).SendBatchAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(m => m.Count == 1),
            Arg.Any<CancellationToken>());

        markedIds.Should().NotBeNull();
        markedIds!.Should().ContainSingle().Which.Should().Be(10L);
    }

    [Fact]
    public async Task StartAsync_PartialConfirmation_OnlyConfirmedIdsMarkedDelivered()
    {
        // Arrange — 3 entries: entries[0] and entries[2] confirmed, entries[1] nacked.
        var entries = new List<OutboxEntry> { CreateEntry(1), CreateEntry(2), CreateEntry(3) };
        bool firstGetPending = true;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (firstGetPending)
                {
                    firstGetPending = false;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(entries);
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<SendResult>>(new SendResult[]
            {
                new(IsConfirmed: true,  DeliveryTag: 0),
                new(IsConfirmed: false, DeliveryTag: 1), // nacked
                new(IsConfirmed: true,  DeliveryTag: 2),
            }));

        IReadOnlyList<long>? capturedIds = null;
        _store
            .MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedIds = ci.Arg<IReadOnlyList<long>>();
                return ValueTask.CompletedTask;
            });

        IReadOnlyList<long>? releasedIds = null;
        _store
            .ReleaseLockAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                releasedIds = ci.Arg<IReadOnlyList<long>>();
                return ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty);
            });

        await using var sut = CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // Assert — only entries[0] (id=1) and entries[2] (id=3) were confirmed; entries[1] (id=2) was nacked.
        capturedIds.Should().NotBeNull();
        capturedIds!.Should().BeEquivalentTo([1L, 3L]);

        // The nacked id (2) must be explicitly released for immediate retry on the next poll cycle.
        await _store.Received().ReleaseLockAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());
        releasedIds.Should().NotBeNull();
        releasedIds!.Should().BeEquivalentTo([2L]);
    }

    [Fact]
    public async Task StopAsync_GracefulShutdown_TerminatesWithoutException()
    {
        // Arrange — polling loop runs indefinitely returning empty; stop must terminate cleanly.
        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>()));

        await using var sut = CreateSut();

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(30); // Let the loop run a few ticks.

        // Act & Assert — StopAsync must complete without throwing.
        Func<Task> stop = () => sut.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DispatchBatchAsync_AllNacked_NoMarkDelivered_RetriesOnNextPoll()
    {
        // Arrange — all messages are nacked by the broker.
        int getPendingCallCount = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                getPendingCallCount++;
                // Fresh entries per poll (each poll rents its own pooled buffers, as in production) so
                // the dispatcher's buffer return stays balanced across re-polls. Entries on the first
                // two polls to verify they remain pending; empty afterwards.
                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                    getPendingCallCount <= 2
                        ? new List<OutboxEntry> { CreateEntry(1), CreateEntry(2), CreateEntry(3) }
                        : Array.Empty<OutboxEntry>());
            });

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<SendResult>>(new SendResult[]
            {
                new(IsConfirmed: false, DeliveryTag: 0),
                new(IsConfirmed: false, DeliveryTag: 1),
                new(IsConfirmed: false, DeliveryTag: 2),
            }));

        IReadOnlyList<long>? releasedIds = null;
        _store
            .ReleaseLockAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                releasedIds = ci.Arg<IReadOnlyList<long>>();
                return ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty);
            });

        await using var sut = CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // Assert — MarkDeliveredAsync must never be called when all messages are nacked.
        await _store.DidNotReceive().MarkDeliveredAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());

        // All nacked ids must be explicitly released so they retry on the next poll cycle
        // (~PollingInterval) instead of waiting for OutboxLockTimeout.
        await _store.Received().ReleaseLockAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());
        releasedIds.Should().NotBeNull();
        releasedIds!.Should().BeEquivalentTo([1L, 2L, 3L]);

        // Adapter was called (messages were sent), but store was polled again (retry).
        await _adapter.Received().SendBatchAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
        getPendingCallCount.Should().BeGreaterThanOrEqualTo(2,
            "the dispatcher must re-poll pending messages when none were confirmed");
    }

    [Fact]
    public async Task DispatchBatchAsync_SendThrows_MessagesRetainedForRetry()
    {
        // Arrange — SendBatchAsync throws a transport exception on the first call.
        int getPendingCallCount = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                getPendingCallCount++;
                // Fresh entries per poll (pooled buffers rented anew each poll, as in production).
                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                    getPendingCallCount <= 2
                        ? new List<OutboxEntry> { CreateEntry(1), CreateEntry(2) }
                        : Array.Empty<OutboxEntry>());
            });

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<SendResult>>>(_ =>
                throw new BareWire.Abstractions.Exceptions.BareWireTransportException(
                    "Connection lost", "RabbitMQ", null));

        await using var sut = CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // Assert — MarkDeliveredAsync must never be called when send throws.
        await _store.DidNotReceive().MarkDeliveredAsync(
            Arg.Any<IReadOnlyList<long>>(),
            Arg.Any<CancellationToken>());

        // Store was polled multiple times — messages are retained for retry.
        getPendingCallCount.Should().BeGreaterThanOrEqualTo(2,
            "the dispatcher must continue polling after a transient send error");
    }

    // U8 — R7.7.6: PerKey barrier blocks confirmed siblings behind a nacked head.
    [Fact]
    public async Task DispatchBatchAsync_PerKey_NackedHead_BlocksSiblings_OtherKeysDelivered()
    {
        // Arrange:
        // Id=1 OrderingKey="K1" — nacked (the nacked head for K1)
        // Id=2 OrderingKey="K1" — confirmed (blocked sibling: Id > firstNackedId for K1)
        // Id=3 OrderingKey="K2" — confirmed (different key, unaffected by K1 barrier)
        // Id=4 OrderingKey=null  — confirmed (keyless, always independent)
        //
        // Expected:
        //   MarkDeliveredAsync([3, 4])
        //   ReleaseLockAsync([1, 2])
        var entries = new List<OutboxEntry>
        {
            CreateKeyedEntry(1, "K1"),
            CreateKeyedEntry(2, "K1"),
            CreateKeyedEntry(3, "K2"),
            CreateKeyedEntry(4, null),
        };

        bool firstGetPending = true;
        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (firstGetPending)
                {
                    firstGetPending = false;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(entries);
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        // Broker: nacks Id=1, confirms Id=2, Id=3, Id=4.
        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<SendResult>>(new SendResult[]
            {
                new(IsConfirmed: false, DeliveryTag: 0), // Id=1 nacked
                new(IsConfirmed: true,  DeliveryTag: 1), // Id=2 confirmed (but blocked)
                new(IsConfirmed: true,  DeliveryTag: 2), // Id=3 confirmed
                new(IsConfirmed: true,  DeliveryTag: 3), // Id=4 confirmed
            }));

        IReadOnlyList<long>? markedIds = null;
        _store
            .MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                markedIds = ci.Arg<IReadOnlyList<long>>();
                return ValueTask.CompletedTask;
            });

        IReadOnlyList<long>? releasedIds = null;
        _store
            .ReleaseLockAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                releasedIds = ci.Arg<IReadOnlyList<long>>();
                return ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty);
            });

        var options = new OutboxOptions
        {
            PollingInterval = TimeSpan.FromMilliseconds(10),
            DispatchBatchSize = 100,
            OrderingMode = BareWire.Abstractions.Outbox.OrderingMode.PerKey,
            OrderingKeyHeaderName = "x-order-key"
        };
        await using var sut = CreateSutWithOptions(options);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // Assert — only K2 (Id=3) and keyless (Id=4) are delivered; K1 (Id=1 nacked + Id=2 blocked) are released.
        markedIds.Should().NotBeNull();
        markedIds!.Should().BeEquivalentTo([3L, 4L],
            "K2 and keyless entries are unaffected by the K1 barrier");

        releasedIds.Should().NotBeNull();
        releasedIds!.Should().BeEquivalentTo([1L, 2L],
            "nacked K1 head (Id=1) and blocked K1 sibling (Id=2) are released for retry");
    }

    // U9 — R7.7.6: None mode path is bit-identical to pre-R7.7.6 — no grouping, no extra calls.
    [Fact]
    public async Task DispatchBatchAsync_None_NackedEntries_PathIdenticalToPreR776_NoGrouping()
    {
        // Arrange — 3 entries with default None ordering: Id=1 confirmed, Id=2 nacked, Id=3 confirmed.
        // The test verifies that:
        //   - MarkDeliveredAsync is called exactly once with [1, 3]
        //   - ReleaseLockAsync is called exactly once with [2]
        //   - No extra calls occur (no grouping artefacts)
        var entries = new List<OutboxEntry>
        {
            CreateEntry(1),
            CreateEntry(2),
            CreateEntry(3),
        };

        bool firstGetPending = true;
        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (firstGetPending)
                {
                    firstGetPending = false;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(entries);
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<SendResult>>(new SendResult[]
            {
                new(IsConfirmed: true,  DeliveryTag: 0), // Id=1
                new(IsConfirmed: false, DeliveryTag: 1), // Id=2 nacked
                new(IsConfirmed: true,  DeliveryTag: 2), // Id=3
            }));

        IReadOnlyList<long>? markedIds = null;
        int markDeliveredCallCount = 0;
        _store
            .MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                markedIds = ci.Arg<IReadOnlyList<long>>();
                markDeliveredCallCount++;
                return ValueTask.CompletedTask;
            });

        IReadOnlyList<long>? releasedIds = null;
        int releaseLockCallCount = 0;
        _store
            .ReleaseLockAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                releasedIds = ci.Arg<IReadOnlyList<long>>();
                releaseLockCallCount++;
                return ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty);
            });

        await using var sut = CreateSut(); // default OutboxOptions: OrderingMode = None

        // Act
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // Assert — exactly one MarkDeliveredAsync call with confirmed ids [1, 3].
        markDeliveredCallCount.Should().Be(1, "None path must issue a single MarkDeliveredAsync call");
        markedIds.Should().NotBeNull();
        markedIds!.Should().BeEquivalentTo([1L, 3L]);

        // Exactly one ReleaseLockAsync call with nacked id [2].
        releaseLockCallCount.Should().Be(1, "None path must issue a single ReleaseLockAsync call");
        releasedIds.Should().NotBeNull();
        releasedIds!.Should().BeEquivalentTo([2L]);
    }

    // Drain-loop: within a SINGLE poll tick the dispatcher keeps claiming while each batch comes
    // back full (more backlog is likely), instead of dispatching one batch per PollingInterval and
    // idling the rest of the tick. Without the drain-loop a single instance is capped at
    // ~DispatchBatchSize per PollingInterval.
    [Fact]
    public async Task RunPollingLoop_FullBatch_DrainsNextBatchWithinSamePollTick()
    {
        // Arrange — batch size 2; the store returns a FULL batch (2 entries) on the first two claims
        // and empties afterwards. With the drain-loop the second claim happens in the SAME tick as
        // the first; without it, the second claim waits a whole PollingInterval for the next tick.
        const int batchSize = 2;
        TimeSpan pollingInterval = TimeSpan.FromMilliseconds(250);

        var secondClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long firstClaimMs = -1;
        long secondClaimMs = -1;
        int getPendingCalls = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int call = Interlocked.Increment(ref getPendingCalls);
                if (call == 1)
                {
                    firstClaimMs = stopwatch.ElapsedMilliseconds;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                        new[] { CreateEntry(1), CreateEntry(2) });
                }

                if (call == 2)
                {
                    secondClaimMs = stopwatch.ElapsedMilliseconds;
                    secondClaim.TrySetResult();
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                        new[] { CreateEntry(3), CreateEntry(4) });
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<OutboundMessage> msgs = ci.Arg<IReadOnlyList<OutboundMessage>>();
                SendResult[] results = msgs
                    .Select((_, i) => new SendResult(IsConfirmed: true, DeliveryTag: (ulong)i))
                    .ToArray();
                return Task.FromResult<IReadOnlyList<SendResult>>(results);
            });

        _store
            .MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);

        await using var sut = CreateSut(pollingInterval: pollingInterval, batchSize: batchSize);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // Wait until the second claim happens. With the drain-loop it lands in the SAME tick as the
        // first (gap ≈ 0); without it, the second claim waits a whole PollingInterval for the next tick.
        await secondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert — the gap between the two claims must be well under one PollingInterval, which is only
        // possible if the dispatcher drains the backlog within a single tick instead of one-per-tick.
        long gapMs = secondClaimMs - firstClaimMs;
        gapMs.Should().BeLessThan(
            (long)(pollingInterval.TotalMilliseconds / 2),
            $"a full batch must trigger an immediate re-claim within the same poll tick (drain-loop); " +
            $"firstClaim@{firstClaimMs}ms secondClaim@{secondClaimMs}ms gap={gapMs}ms " +
            $"pollingInterval={pollingInterval.TotalMilliseconds}ms");
    }

    // Drain-loop failure guard: a FULL batch the broker entirely NACKs must NOT hot-loop. With zero
    // forward progress the dispatcher stops draining and waits one PollingInterval (relative delay)
    // before the next claim, pacing retries instead of hammering a failing broker. So the second claim
    // must land ~one PollingInterval after the first, NOT back-to-back.
    [Fact]
    public async Task RunPollingLoop_FullBatchAllNacked_DoesNotDrain_WaitsForNextTick()
    {
        const int batchSize = 2;
        TimeSpan pollingInterval = TimeSpan.FromMilliseconds(250);

        var secondClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long firstClaimMs = -1;
        long secondClaimMs = -1;
        int getPendingCalls = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int call = Interlocked.Increment(ref getPendingCalls);
                if (call == 1)
                {
                    firstClaimMs = stopwatch.ElapsedMilliseconds;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                        new[] { CreateEntry(1), CreateEntry(2) });
                }

                if (call == 2)
                {
                    secondClaimMs = stopwatch.ElapsedMilliseconds;
                    secondClaim.TrySetResult();
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                        new[] { CreateEntry(3), CreateEntry(4) });
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        // Broker nacks every message — no forward progress.
        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<OutboundMessage> msgs = ci.Arg<IReadOnlyList<OutboundMessage>>();
                SendResult[] results = msgs
                    .Select((_, i) => new SendResult(IsConfirmed: false, DeliveryTag: (ulong)i))
                    .ToArray();
                return Task.FromResult<IReadOnlyList<SendResult>>(results);
            });

        await using var sut = CreateSut(pollingInterval: pollingInterval, batchSize: batchSize);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await secondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert — the all-nacked batch makes no forward progress, so the second claim must wait ~one
        // full PollingInterval for the next tick (no hot-spin against a failing broker).
        long gapMs = secondClaimMs - firstClaimMs;
        gapMs.Should().BeGreaterThanOrEqualTo(
            (long)(pollingInterval.TotalMilliseconds / 2),
            $"an all-nacked full batch makes no forward progress and must wait for the next tick, not " +
            $"drain within the same one; firstClaim@{firstClaimMs}ms secondClaim@{secondClaimMs}ms " +
            $"gap={gapMs}ms pollingInterval={pollingInterval.TotalMilliseconds}ms");
    }

    // Drain-loop failure guard: a FULL batch that is only PARTIALLY confirmed (at least one nack) must
    // NOT drain within the tick. The nacked rows are released for retry; re-claiming them immediately
    // within the same tick would hot-retry the failing subset against a struggling broker. The
    // dispatcher must wait for the next tick, where the released rows retry ~PollingInterval later
    // (the ADR-024 release-on-nack pacing). So the second claim lands on the next tick, not this one.
    [Fact]
    public async Task RunPollingLoop_FullBatchPartiallyNacked_DoesNotDrain_WaitsForNextTick()
    {
        const int batchSize = 2;
        TimeSpan pollingInterval = TimeSpan.FromMilliseconds(250);

        var secondClaim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long firstClaimMs = -1;
        long secondClaimMs = -1;
        int getPendingCalls = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int call = Interlocked.Increment(ref getPendingCalls);
                if (call == 1)
                {
                    firstClaimMs = stopwatch.ElapsedMilliseconds;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                        new[] { CreateEntry(1), CreateEntry(2) });
                }

                if (call == 2)
                {
                    secondClaimMs = stopwatch.ElapsedMilliseconds;
                    secondClaim.TrySetResult();
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                        new[] { CreateEntry(3), CreateEntry(4) });
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        // Broker confirms the first message, nacks the second — a partially failing full batch.
        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<SendResult>>(new SendResult[]
            {
                new(IsConfirmed: true,  DeliveryTag: 0),
                new(IsConfirmed: false, DeliveryTag: 1),
            }));

        await using var sut = CreateSut(pollingInterval: pollingInterval, batchSize: batchSize);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await secondClaim.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert — a partially-nacked batch must not hot-retry the released rows within the tick; the
        // second claim waits ~one full PollingInterval for the next tick.
        long gapMs = secondClaimMs - firstClaimMs;
        gapMs.Should().BeGreaterThanOrEqualTo(
            (long)(pollingInterval.TotalMilliseconds / 2),
            $"a partially-nacked full batch must not drain within the tick (the released nacked rows " +
            $"would be hot-retried); it waits for the next tick. firstClaim@{firstClaimMs}ms " +
            $"secondClaim@{secondClaimMs}ms gap={gapMs}ms pollingInterval={pollingInterval.TotalMilliseconds}ms");
    }

    // Regression (Codex adversarial review): nack→retry pacing must be measured from the NACKED
    // batch's completion, NOT from the polling timer's fixed tick grid. A long same-tick drain (many
    // confirmed full batches) can push a nack close to the next scheduled tick; if the retry is paced
    // by that grid, the released rows are reclaimed almost immediately (far sooner than PollingInterval)
    // against a struggling broker. The next claim after a nack must wait ~PollingInterval from the nack.
    [Fact]
    public async Task RunPollingLoop_NackAfterLongDrain_RetryPacedFromNackNotTimerGrid()
    {
        const int batchSize = 2;
        TimeSpan pollingInterval = TimeSpan.FromMilliseconds(200);

        var reclaimSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long nackMs = -1;
        long reclaimMs = -1;
        int getPendingCalls = 0;
        int sendCalls = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int call = Interlocked.Increment(ref getPendingCalls);
                // Calls 1 and 2: full batches, driving a multi-batch drain within a single tick.
                if (call <= 2)
                {
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                        new[] { CreateEntry(call * 10), CreateEntry(call * 10 + 1) });
                }

                // Call 3: the next claim AFTER the nack — when the released rows would be retried.
                if (call == 3)
                {
                    reclaimMs = stopwatch.ElapsedMilliseconds;
                    reclaimSignal.TrySetResult();
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        // A confirmed batch whose send consumes most of the PollingInterval, so the drain runs long
        // and the subsequent nack lands near the next scheduled tick.
        async Task<IReadOnlyList<SendResult>> DelayedConfirmAsync(IReadOnlyList<OutboundMessage> msgs)
        {
            await Task.Delay(150);
            return msgs.Select((_, i) => new SendResult(IsConfirmed: true, DeliveryTag: (ulong)i)).ToArray();
        }

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                int s = Interlocked.Increment(ref sendCalls);
                IReadOnlyList<OutboundMessage> msgs = ci.Arg<IReadOnlyList<OutboundMessage>>();
                if (s == 1)
                {
                    // First batch: confirmed but slow — consumes most of the interval (long drain).
                    return DelayedConfirmAsync(msgs);
                }

                // Second batch: nacked near the end of the interval. Drain stops; rows are released.
                nackMs = stopwatch.ElapsedMilliseconds;
                return Task.FromResult<IReadOnlyList<SendResult>>(
                    msgs.Select((_, i) => new SendResult(IsConfirmed: false, DeliveryTag: (ulong)i)).ToArray());
            });

        await using var sut = CreateSut(pollingInterval: pollingInterval, batchSize: batchSize);

        // Act
        await sut.StartAsync(CancellationToken.None);
        await reclaimSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert — the next claim after a nack must be paced ~PollingInterval from the NACK, regardless
        // of how long the preceding same-tick drain ran.
        long gapMs = reclaimMs - nackMs;
        gapMs.Should().BeGreaterThanOrEqualTo(
            (long)(pollingInterval.TotalMilliseconds * 0.6),
            $"nack→retry pacing must be relative to the nacked batch completion, not the polling timer " +
            $"grid; nack@{nackMs}ms reclaim@{reclaimMs}ms gap={gapMs}ms " +
            $"pollingInterval={pollingInterval.TotalMilliseconds}ms");
    }

    // Drain throttle (Codex adversarial review): a sustained full + fully-confirmed backlog must NOT
    // re-claim unbounded within a single burst. Each batch costs a claim UPDATE and (on RabbitMQ) a
    // channel open+close, so an unbounded tight drain would churn the DB and broker as fast as the
    // process can loop. The drain is capped: after a bounded burst the loop yields one PollingInterval.
    [Fact]
    public async Task RunPollingLoop_SustainedFullBacklog_DrainBurstIsBounded()
    {
        const int batchSize = 1;
        TimeSpan pollingInterval = TimeSpan.FromMilliseconds(300);
        int getPendingCalls = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                int n = Interlocked.Increment(ref getPendingCalls);
                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(new[] { CreateEntry(n) });
            });

        _adapter
            .SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<OutboundMessage> msgs = ci.Arg<IReadOnlyList<OutboundMessage>>();
                return Task.FromResult<IReadOnlyList<SendResult>>(
                    msgs.Select((_, i) => new SendResult(IsConfirmed: true, DeliveryTag: (ulong)i)).ToArray());
            });

        _store
            .MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);

        await using var sut = CreateSut(pollingInterval: pollingInterval, batchSize: batchSize);

        // Act — run for LESS than one PollingInterval, so only the first drain burst (then the forced
        // pause, not yet elapsed) has happened. StartAsync offloads the loop to the background, so it
        // returns promptly even though this store never yields; the burst cap then bounds the claims.
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await sut.StopAsync(CancellationToken.None);

        // Assert — the burst is bounded (~the internal cap), not the hundreds/thousands an unthrottled
        // tight loop would issue in 150 ms.
        getPendingCalls.Should().BeLessThanOrEqualTo(
            15,
            $"a sustained full backlog must drain in bounded bursts, not unbounded; got {getPendingCalls} " +
            $"claims in <1 PollingInterval ({pollingInterval.TotalMilliseconds}ms)");
    }

    // Host-startup safety (Codex adversarial review): IHostedService.StartAsync runs sequentially during
    // host startup, each instance awaited before the next starts. The dispatcher must offload its polling
    // loop and return immediately — it must NOT run the first poll inline, or a slow/blocking store would
    // delay (and a never-yielding store would hang) host startup. Here the first GetPendingAsync blocks
    // synchronously; StartAsync must still return well before that poll completes.
    [Fact]
    public async Task StartAsync_WhenFirstPollBlocks_ReturnsWithoutRunningItInline()
    {
        using var releaseFirstPoll = new ManualResetEventSlim(initialState: false);

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Simulate a slow/synchronous first poll (e.g. a cold DB connection). Bounded so a
                // regression cannot hang the suite — it self-releases after 5s even if never signalled.
                releaseFirstPoll.Wait(TimeSpan.FromSeconds(5));
                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        await using var sut = CreateSut();

        // Act — call StartAsync on a background thread: on the (buggy) inline path the synchronous
        // dispatch portion runs *before* StartAsync returns, so the CALL itself blocks; running it off
        // the test thread lets us observe whether it returns promptly without deadlocking the test.
        Task startCallReturned = Task.Run(() => sut.StartAsync(CancellationToken.None));
        Task winner = await Task.WhenAny(startCallReturned, Task.Delay(TimeSpan.FromSeconds(2)));

        // Assert — StartAsync returned promptly even though the first poll is still blocked.
        winner.Should().BeSameAs(
            startCallReturned,
            "StartAsync must offload the polling loop and return without executing the first (blocked) poll inline");

        // Cleanup — release the blocked poll and stop the dispatcher.
        releaseFirstPoll.Set();
        await startCallReturned;
        await sut.StopAsync(CancellationToken.None);
    }

    // Startup atomicity (Codex adversarial review): publishing an outbox message and marking it
    // delivered are irreversible external side effects, so the dispatcher must not claim/send until the
    // host has FULLY started. IHostApplicationLifetime.ApplicationStarted does NOT fire if startup aborts
    // (a later IHostedService.StartAsync throws), so gating the loop on it guarantees a process that
    // never became healthy publishes nothing.
    [Fact]
    public async Task StartAsync_DoesNotClaimOrSendUntilApplicationStarted()
    {
        using var appStarted = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(appStarted.Token);

        int getPendingCalls = 0;
        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref getPendingCalls);
                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });

        await using var sut = CreateSut(lifetime: lifetime);

        // Act 1 — started, but the host has NOT signalled ApplicationStarted yet.
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Assert 1 — the loop is gated: no claim has happened.
        Volatile.Read(ref getPendingCalls).Should().Be(
            0,
            "the dispatcher must not poll/claim before the host has fully started");

        // Act 2 — the host completes startup.
        await appStarted.CancelAsync();

        // Wait (bounded) until the loop has polled at least once.
        for (int i = 0; i < 100 && Volatile.Read(ref getPendingCalls) == 0; i++)
        {
            await Task.Delay(10);
        }

        // Assert 2 — once the host has started, the dispatcher begins polling.
        Volatile.Read(ref getPendingCalls).Should().BeGreaterThan(
            0,
            "after ApplicationStarted fires the dispatcher must begin polling");

        await sut.StopAsync(CancellationToken.None);
    }
}
