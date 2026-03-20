// NSubstitute's Returns() for ValueTask-returning mocks triggers CA2012 as a false positive.
// The ValueTask is consumed internally by NSubstitute and never double-consumed.
#pragma warning disable CA2012

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
}
