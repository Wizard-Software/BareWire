#pragma warning disable CA2012 // NSubstitute .Returns() on ValueTask is a known false positive
using System.Collections.Concurrent;
using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Verifies that consumer channels are correctly cleaned up via <see cref="IConsumerChannelManager"/>
/// and that the SettleAsync race condition fix from fc46382 is preserved.
/// </summary>
public sealed class RabbitMqTransportAdapterChannelCleanupTests
{
    private static RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions { ConnectionString = "amqp://guest:guest@localhost:5672/" },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    /// <summary>
    /// Returns the private _activeConsumerChannels field via reflection.
    /// The test assembly has InternalsVisibleTo access to both Abstractions and Transport.RabbitMQ,
    /// but the field itself is private — reflection is the only practical way to seed it without
    /// requiring a real broker connection.
    /// </summary>
    private static ConcurrentDictionary<string, IChannel> GetChannelDictionary(RabbitMqTransportAdapter adapter)
    {
        FieldInfo? field = typeof(RabbitMqTransportAdapter)
            .GetField("_activeConsumerChannels", BindingFlags.NonPublic | BindingFlags.Instance);

        field.Should().NotBeNull("_activeConsumerChannels field must exist on RabbitMqTransportAdapter");

        return (ConcurrentDictionary<string, IChannel>)field!.GetValue(adapter)!;
    }

    private static IChannel CreateMockOpenChannel()
    {
        IChannel mockChannel = Substitute.For<IChannel>();
        mockChannel.IsOpen.Returns(true);
        mockChannel.CloseAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        // Returning a factory delegate avoids CA2012 (ValueTask must not be stored in a variable).
        mockChannel.DisposeAsync().Returns(callInfo => ValueTask.CompletedTask);
        return mockChannel;
    }

    // ── ReleaseConsumerChannelAsync ────────────────────────────────────────────

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenChannelIsRegistered_RemovesChannelFromDictionary()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel mockChannel = CreateMockOpenChannel();

        string channelId = Guid.NewGuid().ToString("N");
        ConcurrentDictionary<string, IChannel> dict = GetChannelDictionary(adapter);
        dict[channelId] = mockChannel;

        // Act
        await adapter.ReleaseConsumerChannelAsync(channelId, CancellationToken.None);

        // Assert — channel must be removed from the tracking dictionary
        dict.ContainsKey(channelId).Should().BeFalse(
            "ReleaseConsumerChannelAsync must remove the channel from _activeConsumerChannels");
    }

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenChannelIsOpen_ClosesAndDisposesChannel()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel mockChannel = CreateMockOpenChannel();

        string channelId = Guid.NewGuid().ToString("N");
        GetChannelDictionary(adapter)[channelId] = mockChannel;

        // Act
        await adapter.ReleaseConsumerChannelAsync(channelId, CancellationToken.None);

        // Assert — CloseAsync and DisposeAsync must each be called exactly once
        await mockChannel.Received(1).CloseAsync(CancellationToken.None);
        await mockChannel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenChannelIsAlreadyClosed_SkipsCloseButStillDisposes()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();

        IChannel mockChannel = Substitute.For<IChannel>();
        mockChannel.IsOpen.Returns(false); // already closed
        mockChannel.DisposeAsync().Returns(callInfo => ValueTask.CompletedTask);

        string channelId = Guid.NewGuid().ToString("N");
        GetChannelDictionary(adapter)[channelId] = mockChannel;

        // Act
        await adapter.ReleaseConsumerChannelAsync(channelId, CancellationToken.None);

        // Assert — CloseAsync must NOT be called; DisposeAsync must still be called
        await mockChannel.DidNotReceive().CloseAsync(Arg.Any<CancellationToken>());
        await mockChannel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenCalledTwiceWithSameId_SecondCallIsNoOp()
    {
        // Arrange — regression test for fc46382:
        // The channel must remain accessible for SettleAsync until ReleaseConsumerChannelAsync is
        // called. The second call (e.g. if the pipeline calls it twice during error recovery) must
        // be a safe no-op — CloseAsync must never be called more than once on the same channel.
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel mockChannel = CreateMockOpenChannel();

        string channelId = Guid.NewGuid().ToString("N");
        GetChannelDictionary(adapter)[channelId] = mockChannel;

        // Act — call twice
        await adapter.ReleaseConsumerChannelAsync(channelId, CancellationToken.None);
        await adapter.ReleaseConsumerChannelAsync(channelId, CancellationToken.None);

        // Assert — CloseAsync and DisposeAsync each called exactly once despite two Release calls
        await mockChannel.Received(1).CloseAsync(CancellationToken.None);
        await mockChannel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenIdIsUnknown_DoesNotThrow()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();

        // Act — release a channel ID that was never registered
        Func<Task> act = () => adapter.ReleaseConsumerChannelAsync("nonexistent-id", CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    // ── DisposeAsync — channels remaining after release ────────────────────────

    [Fact]
    public async Task DisposeAsync_WhenChannelAlreadyReleased_DoesNotAttemptToCloseItAgain()
    {
        // Arrange — simulates the normal path: release happens before dispose
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel mockChannel = CreateMockOpenChannel();

        string channelId = Guid.NewGuid().ToString("N");
        GetChannelDictionary(adapter)[channelId] = mockChannel;

        // Release first (normal pipeline path)
        await adapter.ReleaseConsumerChannelAsync(channelId, CancellationToken.None);

        // Act — dispose the adapter
        await adapter.DisposeAsync();

        // Assert — CloseAsync was called exactly once (by Release, not by Dispose)
        await mockChannel.Received(1).CloseAsync(Arg.Any<CancellationToken>());
        await mockChannel.Received(1).DisposeAsync();
    }
}
