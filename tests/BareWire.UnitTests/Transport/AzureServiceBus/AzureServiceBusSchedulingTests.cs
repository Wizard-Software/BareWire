using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>
/// Pure-mapping unit tests for the native scheduling path. These tests exercise the
/// <see cref="TransportNativeScheduleProvider"/>-to-<see cref="INativeMessageScheduler"/>
/// contract — specifically verifying that the OutboundMessage constructed by the provider
/// carries the correct routing key, content type, headers, and that the returned token
/// carries the correct destination (RoutingKey). No broker I/O.
/// Full E2E schedule/deliver/cancel against an ASB emulator belongs to R2.5.
/// </summary>
public sealed class AzureServiceBusSchedulingTests
{
    private sealed record ShipmentTimeout(Guid ShipmentId);

    private static (
        TransportNativeScheduleProvider provider,
        INativeMessageScheduler scheduler,
        IMessageSerializer serializer) CreateProvider()
    {
        var scheduler = Substitute.For<INativeMessageScheduler>();
        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        var logger = NullLogger<TransportNativeScheduleProvider>.Instance;
        var timeProvider = new FakeTimeProvider();

        var provider = new TransportNativeScheduleProvider(scheduler, serializer, logger, timeProvider);
        return (provider, scheduler, serializer);
    }

    [Fact]
    public async Task ScheduleAsync_OutboundMessage_HasCorrectRoutingKey()
    {
        var (provider, scheduler, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        const string destination = "shipment-saga";
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(new ScheduledMessageToken(1L, destination));

        await provider.ScheduleAsync(new ShipmentTimeout(correlationId), TimeSpan.FromMinutes(30), destination, correlationId);

        await scheduler.Received(1).ScheduleAsync(
            Arg.Is<OutboundMessage>(m => m.RoutingKey == destination),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_OutboundMessage_HasContentType()
    {
        var (provider, scheduler, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(new ScheduledMessageToken(2L, "q"));

        await provider.ScheduleAsync(new ShipmentTimeout(correlationId), TimeSpan.FromMinutes(1), "q", correlationId);

        await scheduler.Received(1).ScheduleAsync(
            Arg.Is<OutboundMessage>(m => m.ContentType == "application/json"),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_OutboundMessage_HasBwMessageTypeHeader()
    {
        var (provider, scheduler, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(new ScheduledMessageToken(3L, "q"));

        await provider.ScheduleAsync(new ShipmentTimeout(correlationId), TimeSpan.FromMinutes(1), "q", correlationId);

        await scheduler.Received(1).ScheduleAsync(
            Arg.Is<OutboundMessage>(m =>
                m.Headers.ContainsKey("BW-MessageType") &&
                m.Headers["BW-MessageType"] == nameof(ShipmentTimeout)),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_OutboundMessage_HasCorrelationIdHeader()
    {
        var (provider, scheduler, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(new ScheduledMessageToken(4L, "q"));

        await provider.ScheduleAsync(new ShipmentTimeout(correlationId), TimeSpan.FromMinutes(1), "q", correlationId);

        await scheduler.Received(1).ScheduleAsync(
            Arg.Is<OutboundMessage>(m =>
                m.Headers.ContainsKey("correlation-id") &&
                m.Headers["correlation-id"] == correlationId.ToString()),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that the returned token carries the destination (RoutingKey) so that
    /// <see cref="INativeMessageScheduler.CancelScheduledAsync"/> can resolve the correct
    /// sender without additional state (D-GAP-1b / token cancel-sufficient).
    /// </summary>
    [Fact]
    public async Task ScheduleAsync_ReturnedToken_DestinationEqualsRoutingKey()
    {
        var (provider, scheduler, _) = CreateProvider();
        var correlationId = Guid.NewGuid();
        const string destination = "my-destination-queue";
        const long seq = 999L;

        // The adapter returns a token with Destination == RoutingKey; we simulate that here.
        scheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>())
            .Returns(callInfo =>
            {
                var msg = callInfo.ArgAt<OutboundMessage>(0);
                return new ScheduledMessageToken(seq, msg.RoutingKey);
            });

        await provider.ScheduleAsync(new ShipmentTimeout(correlationId), TimeSpan.FromMinutes(1), destination, correlationId);

        // Capture the token via CancelAsync — if destination is wrong, cancel won't find the right sender.
        await provider.CancelAsync<ShipmentTimeout>(correlationId);

        await scheduler.Received(1).CancelScheduledAsync(
            Arg.Is<ScheduledMessageToken>(t => t.Destination == destination && t.SequenceNumber == seq),
            Arg.Any<CancellationToken>());
    }
}
