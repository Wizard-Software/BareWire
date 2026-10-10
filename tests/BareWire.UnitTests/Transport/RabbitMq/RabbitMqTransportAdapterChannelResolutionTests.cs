#pragma warning disable CA2012 // NSubstitute .Returns() on ValueTask is a known false positive
using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Verifies that settlement and durable park resolve the consumer channel strictly through the
/// BW-ConsumerChannelId header, never through a heuristic choice of another channel.
/// </summary>
public sealed class RabbitMqTransportAdapterChannelResolutionTests
{
    private const string ChannelIdHeader = "BW-ConsumerChannelId";

    private static RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions { ConnectionString = "amqp://guest:guest@localhost:5672/" },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static ConcurrentDictionary<string, IChannel> GetChannelDictionary(RabbitMqTransportAdapter adapter)
    {
        FieldInfo? field = typeof(RabbitMqTransportAdapter)
            .GetField("_activeConsumerChannels", BindingFlags.NonPublic | BindingFlags.Instance);

        field.Should().NotBeNull("_activeConsumerChannels field must exist on RabbitMqTransportAdapter");

        return (ConcurrentDictionary<string, IChannel>)field!.GetValue(adapter)!;
    }

    private static IChannel CreateMockOpenChannel()
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        channel.CloseAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        channel.DisposeAsync().Returns(_ => ValueTask.CompletedTask);
        channel.BasicAckAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        channel.BasicNackAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        channel.BasicRejectAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        return channel;
    }

    private static InboundMessage CreateMessage(string? channelIdHeader)
    {
        Dictionary<string, string> headers = [];
        if (channelIdHeader is not null)
        {
            headers[ChannelIdHeader] = channelIdHeader;
        }

        return new InboundMessage("m-1", headers, ReadOnlySequence<byte>.Empty, deliveryTag: 7);
    }

    private static void AssertNoSettlement(IChannel channel)
    {
        channel.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name is "BasicAckAsync" or "BasicNackAsync" or "BasicRejectAsync")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(SettlementAction.Ack)]
    [InlineData(SettlementAction.Nack)]
    [InlineData(SettlementAction.Reject)]
    [InlineData(SettlementAction.Requeue)]
    [InlineData(SettlementAction.Defer)]
    public async Task SettleAsync_WhenHeaderChannelUnknownAndOtherChannelsActive_ThrowsAndSettlesOnNoChannel(
        SettlementAction action)
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel channelA = CreateMockOpenChannel();
        IChannel channelB = CreateMockOpenChannel();
        GetChannelDictionary(adapter)["ch-a"] = channelA;
        GetChannelDictionary(adapter)["ch-b"] = channelB;
        using InboundMessage message = CreateMessage("unknown");

        // Act
        Func<Task> settle = () => adapter.SettleAsync(action, message, CancellationToken.None);

        // Assert
        await settle.Should().ThrowAsync<BareWireTransportException>();
        AssertNoSettlement(channelA);
        AssertNoSettlement(channelB);
    }

    [Fact]
    public async Task SettleAsync_WhenHeaderMissingAndSingleChannelActive_Throws()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel channel = CreateMockOpenChannel();
        GetChannelDictionary(adapter)["ch-a"] = channel;
        using InboundMessage message = CreateMessage(null);

        // Act
        Func<Task> settle = () => adapter.SettleAsync(SettlementAction.Ack, message, CancellationToken.None);

        // Assert
        await settle.Should().ThrowAsync<BareWireTransportException>().WithMessage("*no BW-ConsumerChannelId header*");
        AssertNoSettlement(channel);
    }

    [Fact]
    public async Task SettleAsync_WhenHeaderMissingAndChannelsActive_Throws()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel channelA = CreateMockOpenChannel();
        IChannel channelB = CreateMockOpenChannel();
        GetChannelDictionary(adapter)["ch-a"] = channelA;
        GetChannelDictionary(adapter)["ch-b"] = channelB;
        using InboundMessage message = CreateMessage(null);

        // Act
        Func<Task> settle = () => adapter.SettleAsync(SettlementAction.Ack, message, CancellationToken.None);

        // Assert
        await settle.Should().ThrowAsync<BareWireTransportException>();
        AssertNoSettlement(channelA);
        AssertNoSettlement(channelB);
    }

    [Fact]
    public async Task SettleAsync_WhenHeaderChannelReleasedAndOtherEndpointChannelActive_ThrowsAndDoesNotSettle()
    {
        // Arrange — the delivering channel was released; the dictionary holds only another endpoint's channel.
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel otherEndpointChannel = CreateMockOpenChannel();
        GetChannelDictionary(adapter)["ch-other"] = otherEndpointChannel;
        using InboundMessage message = CreateMessage("ch-released");

        // Act
        Func<Task> settle = () => adapter.SettleAsync(SettlementAction.Ack, message, CancellationToken.None);

        // Assert
        await settle.Should().ThrowAsync<BareWireTransportException>().WithMessage("*'ch-released'*");
        AssertNoSettlement(otherEndpointChannel);
    }

    [Fact]
    public async Task SettleAsync_WhenHeaderChannelIdExceeds64Characters_TruncatesIdInExceptionMessage()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();
        string longId = new('x', 100);
        using InboundMessage message = CreateMessage(longId);

        // Act
        Func<Task> settle = () => adapter.SettleAsync(SettlementAction.Ack, message, CancellationToken.None);

        // Assert
        BareWireTransportException ex = (await settle.Should().ThrowAsync<BareWireTransportException>()).Which;
        ex.Message.Should().Contain($"'{new string('x', 64)}'");
        ex.Message.Should().NotContain(new string('x', 65));
    }

    [Fact]
    public async Task SettleAsync_WhenHeaderChannelActive_SettlesOnThatChannelOnly()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel channelA = CreateMockOpenChannel();
        IChannel channelB = CreateMockOpenChannel();
        GetChannelDictionary(adapter)["ch-a"] = channelA;
        GetChannelDictionary(adapter)["ch-b"] = channelB;
        using InboundMessage message = CreateMessage("ch-b");

        // Act
        await adapter.SettleAsync(SettlementAction.Ack, message, CancellationToken.None);

        // Assert
        await channelB.Received(1).BasicAckAsync(7UL, false, Arg.Any<CancellationToken>());
        AssertNoSettlement(channelA);
    }

    [Fact]
    public async Task ParkHeadDurablyAsync_WhenHeaderChannelUnknown_ReturnsNotFoundWithoutSettling()
    {
        // Arrange
        RabbitMqTransportAdapter adapter = CreateAdapter();
        IChannel channelA = CreateMockOpenChannel();
        IChannel channelB = CreateMockOpenChannel();
        GetChannelDictionary(adapter)["ch-a"] = channelA;
        GetChannelDictionary(adapter)["ch-b"] = channelB;
        using InboundMessage message = CreateMessage("unknown");

        // Act
        DurableSettlementResult result = await adapter.ParkHeadDurablyAsync(
            message, "dlx", "dlq", CancellationToken.None);

        // Assert
        result.IsDurablyConfirmed.Should().BeFalse();
        result.FailureReason.Should().Be("consumer channel not found");
        AssertNoSettlement(channelA);
        AssertNoSettlement(channelB);
    }
}
