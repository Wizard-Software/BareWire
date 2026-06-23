// NSubstitute's Returns() for ValueTask-returning mocks triggers CA2012 as a false positive.
// The ValueTask is consumed internally by NSubstitute and never double-consumed.
#pragma warning disable CA2012

using System.Collections.Frozen;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Microsoft.Extensions.DependencyInjection;
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

    private OutboxDispatcher CreateSut(TimeSpan? pollingInterval = null, int batchSize = 100)
    {
        var options = new OutboxOptions
        {
            PollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(10),
            DispatchBatchSize = batchSize
        };
        return new OutboxDispatcher(_scopeFactory, _adapter, options, _logger);
    }

    private OutboxDispatcher CreateSutWithOptions(OutboxOptions options)
        => new OutboxDispatcher(_scopeFactory, _adapter, options, _logger);

    private static OutboxEntry CreateEntry(long id, string routingKey = "test.routing.key")
    {
        byte[] body = "test-body"u8.ToArray();
        return new OutboxEntry
        {
            Id = id,
            RoutingKey = routingKey,
            Headers = new Dictionary<string, string>(),
            PooledBody = body,
            BodyLength = body.Length,
            ContentType = "application/json",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = OutboxEntryStatus.Pending
        };
    }

    private static OutboxEntry CreateKeyedEntry(long id, string? orderingKey, string routingKey = "test.routing.key")
    {
        byte[] body = "test-body"u8.ToArray();
        return new OutboxEntry
        {
            Id = id,
            RoutingKey = routingKey,
            Headers = new Dictionary<string, string>(),
            PooledBody = body,
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
        var entries = new List<OutboxEntry> { CreateEntry(1), CreateEntry(2), CreateEntry(3) };
        int getPendingCallCount = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                getPendingCallCount++;
                // Return entries on every poll to verify they remain pending.
                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                    getPendingCallCount <= 2 ? entries : Array.Empty<OutboxEntry>());
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
        var entries = new List<OutboxEntry> { CreateEntry(1), CreateEntry(2) };
        int getPendingCallCount = 0;

        _store
            .GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                getPendingCallCount++;
                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(
                    getPendingCallCount <= 2 ? entries : Array.Empty<OutboxEntry>());
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
}
