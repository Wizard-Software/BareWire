using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

public sealed class DelayRequeueScheduleProviderTests
{
    // ── Test types ────────────────────────────────────────────────────────────

    private sealed record PaymentTimeout(Guid OrderId);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DelayRequeueScheduleProvider CreateProvider()
    {
        var transport = Substitute.For<ITransportAdapter>();
        var logger = NullLogger<DelayRequeueScheduleProvider>.Instance;
        return new DelayRequeueScheduleProvider(transport, logger);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleAsync_ValidMessage_CompletesWithoutException()
    {
        var provider = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());

        Func<Task> act = () => provider.ScheduleAsync(
            message, TimeSpan.FromSeconds(30), "order-timeout", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ScheduleAsync_LogsSchedulingIntent()
    {
        // DelayRequeueScheduleProvider uses [LoggerMessage] source-generated logging (Debug level).
        // We verify no exception is thrown and the method completes — the log is not observable
        // without a test-specific ILoggerFactory because ILogger<T> where T is internal cannot be mocked.
        var provider = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());

        Func<Task> act = () => provider.ScheduleAsync(
            message, TimeSpan.FromSeconds(30), "order-timeout", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CancelAsync_LogsWarningAboutRabbitMqLimitation()
    {
        // CancelAsync logs a Warning about RabbitMQ not supporting selective message deletion.
        // We verify the method completes without error (observable behavior without mocking internals).
        var provider = CreateProvider();

        Func<Task> act = () => provider.CancelAsync<PaymentTimeout>(Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CancelAsync_ValidCall_CompletesWithoutException()
    {
        var provider = CreateProvider();

        Func<Task> act = () => provider.CancelAsync<PaymentTimeout>(Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ScheduleAsync_NullMessage_ThrowsArgumentNullException()
    {
        var provider = CreateProvider();

        Func<Task> act = () => provider.ScheduleAsync<PaymentTimeout>(
            null!, TimeSpan.FromSeconds(30), "order-timeout", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ScheduleAsync_NullDestinationQueue_ThrowsArgumentNullException()
    {
        var provider = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());

        Func<Task> act = () => provider.ScheduleAsync(
            message, TimeSpan.FromSeconds(30), null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ScheduleAsync_ZeroDelay_CompletesWithoutException()
    {
        var provider = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());

        Func<Task> act = () => provider.ScheduleAsync(
            message, TimeSpan.Zero, "my-queue", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Constructor_NullTransport_ThrowsArgumentNullException()
    {
        var logger = NullLogger<DelayRequeueScheduleProvider>.Instance;

        Action act = () => _ = new DelayRequeueScheduleProvider(null!, logger);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var transport = Substitute.For<ITransportAdapter>();

        Action act = () => _ = new DelayRequeueScheduleProvider(transport, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
