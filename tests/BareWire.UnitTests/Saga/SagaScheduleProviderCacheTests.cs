using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

public sealed class SagaScheduleProviderCacheTests
{
    private static ITransportAdapter NativeTransport()
    {
        var transport = Substitute.For<ITransportAdapter, INativeMessageScheduler>();
        transport.TransportName.Returns("native");
        return transport;
    }

    [Fact]
    public void GetOrCreate_CalledTwice_ReturnsSameInstance()
    {
        var cache = new SagaScheduleProviderCache();
        var transport = NativeTransport();
        var serializer = Substitute.For<IMessageSerializer>();

        IScheduleProvider first = cache.GetOrCreate(transport, serializer, NullLoggerFactory.Instance);
        IScheduleProvider second = cache.GetOrCreate(transport, serializer, NullLoggerFactory.Instance);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void GetOrCreate_WithNativeSchedulerTransport_ReturnsTransportNativeProvider() =>
        new SagaScheduleProviderCache()
            .GetOrCreate(NativeTransport(), Substitute.For<IMessageSerializer>(), NullLoggerFactory.Instance)
            .Should().BeOfType<TransportNativeScheduleProvider>();

    [Fact]
    public void GetOrCreate_WithTransportWithoutNativeScheduler_ReturnsNewDelayRequeueProviderEachCall()
    {
        var cache = new SagaScheduleProviderCache();
        var transport = Substitute.For<ITransportAdapter>();
        var serializer = Substitute.For<IMessageSerializer>();

        IScheduleProvider first = cache.GetOrCreate(transport, serializer, NullLoggerFactory.Instance);
        IScheduleProvider second = cache.GetOrCreate(transport, serializer, NullLoggerFactory.Instance);

        first.Should().BeOfType<DelayRequeueScheduleProvider>();
        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentFirstCalls_AllReturnSameInstance()
    {
        var cache = new SagaScheduleProviderCache();
        var transport = NativeTransport();
        var serializer = Substitute.For<IMessageSerializer>();

        IScheduleProvider[] results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => cache.GetOrCreate(transport, serializer, NullLoggerFactory.Instance))));

        results.Distinct().Should().ContainSingle();
    }
}
