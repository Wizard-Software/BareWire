using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

public sealed class ScheduleProviderFactoryTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ITransportAdapter CreateTransportAdapter()
    {
        var adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("RabbitMQ");
        return adapter;
    }

    private static NullLoggerFactory CreateLoggerFactory()
        => NullLoggerFactory.Instance;

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_Auto_ReturnsDelayRequeueProvider()
    {
        var transport = CreateTransportAdapter();
        var loggerFactory = CreateLoggerFactory();

        var provider = ScheduleProviderFactory.Create(SchedulingStrategy.Auto, transport, loggerFactory);

        provider.Should().BeOfType<DelayRequeueScheduleProvider>();
    }

    [Fact]
    public void Create_DelayRequeue_ReturnsDelayRequeueProvider()
    {
        var transport = CreateTransportAdapter();
        var loggerFactory = CreateLoggerFactory();

        var provider = ScheduleProviderFactory.Create(SchedulingStrategy.DelayRequeue, transport, loggerFactory);

        provider.Should().BeOfType<DelayRequeueScheduleProvider>();
    }

    [Fact]
    public void Create_TransportNative_ThrowsNotSupportedException()
    {
        var transport = CreateTransportAdapter();
        var loggerFactory = CreateLoggerFactory();

        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.TransportNative, transport, loggerFactory);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Create_ExternalScheduler_ThrowsNotSupportedException()
    {
        var transport = CreateTransportAdapter();
        var loggerFactory = CreateLoggerFactory();

        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.ExternalScheduler, transport, loggerFactory);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Create_DelayTopic_ThrowsNotSupportedException()
    {
        var transport = CreateTransportAdapter();
        var loggerFactory = CreateLoggerFactory();

        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.DelayTopic, transport, loggerFactory);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Create_NullTransport_ThrowsArgumentNullException()
    {
        var loggerFactory = CreateLoggerFactory();

        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.Auto, null!, loggerFactory);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_NullLoggerFactory_ThrowsArgumentNullException()
    {
        var transport = CreateTransportAdapter();

        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.Auto, transport, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
