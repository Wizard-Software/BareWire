using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

public sealed class ScheduleProviderFactoryTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    // A transport that does NOT implement INativeMessageScheduler (e.g. RabbitMQ)
    private static ITransportAdapter CreateNonNativeTransport()
    {
        var adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("RabbitMQ");
        return adapter;
    }

    // A transport that implements INativeMessageScheduler (e.g. Azure Service Bus)
    private static ITransportAdapter CreateNativeTransport()
    {
        var adapter = Substitute.For<ITransportAdapter, INativeMessageScheduler>();
        adapter.TransportName.Returns("AzureServiceBus");
        return adapter;
    }

    private static NullLoggerFactory CreateLoggerFactory()
        => NullLoggerFactory.Instance;

    private static IMessageSerializer CreateSerializer()
    {
        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        return serializer;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_Auto_NonNativeTransport_ReturnsDelayRequeueProvider()
    {
        var transport = CreateNonNativeTransport();

        var provider = ScheduleProviderFactory.Create(SchedulingStrategy.Auto, transport, CreateLoggerFactory(), CreateSerializer());

        provider.Should().BeOfType<DelayRequeueScheduleProvider>();
    }

    [Fact]
    public void Create_Auto_NativeTransport_ReturnsTransportNativeProvider()
    {
        var transport = CreateNativeTransport();

        var provider = ScheduleProviderFactory.Create(SchedulingStrategy.Auto, transport, CreateLoggerFactory(), CreateSerializer());

        provider.Should().BeOfType<TransportNativeScheduleProvider>();
    }

    [Fact]
    public void Create_DelayRequeue_ReturnsDelayRequeueProvider()
    {
        var provider = ScheduleProviderFactory.Create(SchedulingStrategy.DelayRequeue, CreateNonNativeTransport(), CreateLoggerFactory(), CreateSerializer());

        provider.Should().BeOfType<DelayRequeueScheduleProvider>();
    }

    [Fact]
    public void Create_TransportNative_WithNativeTransport_ReturnsTransportNativeProvider()
    {
        var transport = CreateNativeTransport();

        var provider = ScheduleProviderFactory.Create(SchedulingStrategy.TransportNative, transport, CreateLoggerFactory(), CreateSerializer());

        provider.Should().BeOfType<TransportNativeScheduleProvider>();
    }

    [Fact]
    public void Create_TransportNative_WithNonNativeTransport_ThrowsDescriptiveNotSupportedException()
    {
        var transport = CreateNonNativeTransport();

        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.TransportNative, transport, CreateLoggerFactory(), CreateSerializer());

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*RabbitMQ*");
    }

    [Fact]
    public void Create_ExternalScheduler_ThrowsNotSupportedException()
    {
        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.ExternalScheduler, CreateNonNativeTransport(), CreateLoggerFactory(), CreateSerializer());

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Create_DelayTopic_ThrowsNotSupportedException()
    {
        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.DelayTopic, CreateNonNativeTransport(), CreateLoggerFactory(), CreateSerializer());

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Create_NullTransport_ThrowsArgumentNullException()
    {
        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.Auto, null!, CreateLoggerFactory(), CreateSerializer());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_NullLoggerFactory_ThrowsArgumentNullException()
    {
        Action act = () => ScheduleProviderFactory.Create(SchedulingStrategy.Auto, CreateNonNativeTransport(), null!, CreateSerializer());

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// PERF-1 verification: a custom maxTokens passed to Create flows through to the
    /// TransportNativeScheduleProvider and enforces the cap (schedule 3 messages with
    /// maxTokens: 2 — TokenCount must not exceed 2).
    /// </summary>
    [Fact]
    public async Task Create_TransportNative_WithCustomMaxTokens_CapIsHonored()
    {
        const int customMax = 2;
        var transport = CreateNativeTransport();
        var nativeScheduler = (INativeMessageScheduler)transport;
        nativeScheduler.ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new ScheduledMessageToken(1L, "q"));

        var provider = ScheduleProviderFactory.Create(
            SchedulingStrategy.TransportNative, transport, CreateLoggerFactory(), CreateSerializer(),
            maxTokens: customMax);

        var nativeProvider = provider.Should().BeOfType<TransportNativeScheduleProvider>().Subject;

        // Schedule 3 messages against a cap of 2 — oldest is evicted on the 3rd insert
        for (int i = 0; i < customMax + 1; i++)
        {
            await nativeProvider.ScheduleAsync(
                new object(), TimeSpan.FromMinutes(i + 1), "q", Guid.NewGuid());
        }

        nativeProvider.TokenCount.Should().Be(customMax);
    }
}
