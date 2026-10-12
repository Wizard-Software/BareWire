using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
using NSubstitute;
using Xunit;
using static BareWire.UnitTests.Transport.AzureServiceBus.AzureServiceBusShutdownTestSupport;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

public sealed class AzureServiceBusFlowControlValidationTests
{
    // Every case must be rejected before any receiver or session is opened. The doubles below block until the
    // consume token is cancelled, so an adapter that accepts the options just waits (no hot loop) and the test
    // then fails on the assertions instead of spinning.
    //
    // Observed against the code before the validation existed: the three Drop* cases, the MaxInFlightMessages
    // cases and the non-session InternalQueueCapacity case fail (accepted, or rejected only after the receiver
    // was opened). The session case with InternalQueueCapacity -1 already passes, because the session path
    // rejects it at channel creation before a session is accepted; it is a regression guard, not a reproduction.
    [Theory]
    [InlineData(false, BoundedChannelFullMode.DropWrite, 100, 1000)]
    [InlineData(false, BoundedChannelFullMode.DropOldest, 100, 1000)]
    [InlineData(false, BoundedChannelFullMode.DropNewest, 100, 1000)]
    [InlineData(false, BoundedChannelFullMode.Wait, 0, 1000)]
    [InlineData(false, BoundedChannelFullMode.Wait, 100, 0)]
    [InlineData(true, BoundedChannelFullMode.DropWrite, 100, 1000)]
    [InlineData(true, BoundedChannelFullMode.Wait, -1, 1000)]
    [InlineData(true, BoundedChannelFullMode.Wait, 100, -1)]
    public async Task ConsumeAsync_InvalidFlowControl_ThrowsBeforeOpeningReceiver(
        bool sessions, BoundedChannelFullMode fullMode, int maxInFlightMessages, int internalQueueCapacity)
    {
        // Arrange
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        ProgramReceive(receiver, []);

        ServiceBusClient client = Substitute.For<ServiceBusClient>();
        client.CreateReceiver(QueueName, Arg.Any<ServiceBusReceiverOptions>()).Returns(receiver);
        client
            .AcceptNextSessionAsync(
                QueueName, Arg.Any<ServiceBusSessionReceiverOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
                return null!;
            });

        var adapter = new AzureServiceBusTransportAdapter(
            Options(sessions), new CapturingLogger(), client)
        {
            SettlementBudget = TimeSpan.Zero,
        };

        var flowControl = new FlowControlOptions
        {
            MaxInFlightMessages = maxInFlightMessages,
            InternalQueueCapacity = internalQueueCapacity,
            FullMode = fullMode,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<InboundMessage> enumerator = adapter
            .ConsumeAsync(QueueName, flowControl, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        try
        {
            // Act
            Func<Task> act = async () => await enumerator.MoveNextAsync();

            // Assert
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            client.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
                .Should().NotContain(nameof(ServiceBusClient.CreateReceiver))
                .And.NotContain(nameof(ServiceBusClient.AcceptNextSessionAsync));
        }
        finally
        {
            await cts.CancelAsync();
        }
    }
}
