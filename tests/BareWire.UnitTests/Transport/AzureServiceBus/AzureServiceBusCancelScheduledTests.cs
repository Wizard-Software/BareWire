using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using BareWire.Transport.AzureServiceBus;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>
/// Unit tests for <see cref="AzureServiceBusTransportAdapter.TryCancelScheduledMessageAsync"/> — the
/// internal helper that tolerates a broker-reported <see cref="ServiceBusFailureReason.MessageNotFound"/>
/// when cancelling a scheduled message that has already been delivered or already cancelled.
/// <see cref="ServiceBusSender"/> has a protected constructor and virtual members, so it can be
/// substituted directly with NSubstitute without an emulator or a live broker connection.
/// </summary>
public sealed class AzureServiceBusCancelScheduledTests
{
    [Fact]
    public async Task TryCancelScheduledMessageAsync_WhenBrokerReportsMessageNotFound_ReturnsFalse()
    {
        var sender = Substitute.For<ServiceBusSender>();
        sender.CancelScheduledMessageAsync(42L, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceBusException("gone", ServiceBusFailureReason.MessageNotFound));

        bool result = await AzureServiceBusTransportAdapter.TryCancelScheduledMessageAsync(
            sender, 42L, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryCancelScheduledMessageAsync_WhenCancelSucceeds_ReturnsTrue()
    {
        var sender = Substitute.For<ServiceBusSender>();
        sender.CancelScheduledMessageAsync(7L, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        bool result = await AzureServiceBusTransportAdapter.TryCancelScheduledMessageAsync(
            sender, 7L, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TryCancelScheduledMessageAsync_WhenOtherServiceBusFailure_Propagates()
    {
        var sender = Substitute.For<ServiceBusSender>();
        sender.CancelScheduledMessageAsync(13L, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceBusException("timed out", ServiceBusFailureReason.ServiceTimeout));

        Func<Task> act = () => AzureServiceBusTransportAdapter.TryCancelScheduledMessageAsync(
            sender, 13L, CancellationToken.None);

        await act.Should().ThrowAsync<ServiceBusException>();
    }
}
