using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Testing;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Testing;

public sealed class ObservingTransportAdapterTests
{
    private static ITransportAdapter InMemoryShapedInner()
        => (ITransportAdapter)Substitute.For(
            [typeof(ITransportAdapter), typeof(IGracefulDrainTransport), typeof(ITransportHealthSource),
             typeof(INativeMessageScheduler), typeof(IAsyncDisposable)],
            []);

    private static OutboundMessage CreateOutbound(string routingKey)
        => new(routingKey, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, "application/json");

    [Fact]
    public async Task SeamSet_MirrorsInnerTransport()
    {
        // Real inner transport taken from a harness — the decorator must answer every seam
        // cast exactly like it does, without adding or dropping any.
        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        ITransportAdapter inner = harness.Adapter.Inner;

        Type[] seams =
        [
            typeof(IGracefulDrainTransport), typeof(ITransportHealthSource), typeof(INativeMessageScheduler),
            typeof(IConsumerChannelManager), typeof(IDurableParkSettlement), typeof(IAsyncDisposable), typeof(IDisposable),
        ];

        foreach (Type seam in seams)
            seam.IsInstanceOfType(harness.Adapter).Should().Be(seam.IsInstanceOfType(inner), seam.Name);
    }

    [Fact]
    public async Task DrainAsync_DelegatesToInnerAndCounts()
    {
        ITransportAdapter inner = InMemoryShapedInner();
        ObservingTransportAdapter sut = new(inner);

        await sut.DrainAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        await ((IGracefulDrainTransport)inner).Received(1)
            .DrainAsync(TimeSpan.FromSeconds(1), Arg.Any<CancellationToken>());
        sut.DrainCallCount.Should().Be(1);
    }

    [Fact]
    public void GetHealth_DelegatesToInner()
    {
        ITransportAdapter inner = InMemoryShapedInner();
        BusHealthStatus status = new(BusStatus.Healthy, "all good", []);
        ((ITransportHealthSource)inner).GetHealth().Returns(status);
        ObservingTransportAdapter sut = new(inner);

        sut.GetHealth().Should().BeSameAs(status);
    }

    [Fact]
    public void TransportNameAndCapabilities_DelegateToInner()
    {
        ITransportAdapter inner = InMemoryShapedInner();
        inner.TransportName.Returns("InMemory");
        inner.Capabilities.Returns(TransportCapabilities.NativeScheduling);
        ObservingTransportAdapter sut = new(inner);

        sut.TransportName.Should().Be("InMemory");
        sut.Capabilities.Should().Be(TransportCapabilities.NativeScheduling);
    }

    [Fact]
    public async Task SendBatchAsync_RaisesMessageSentForEveryMessage_AndReturnsInnerResults()
    {
        ITransportAdapter inner = InMemoryShapedInner();
        OutboundMessage first = CreateOutbound("a");
        OutboundMessage second = CreateOutbound("b");
        IReadOnlyList<SendResult> innerResults = [new SendResult(false, 7UL), new SendResult(true, 8UL)];
        inner.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(innerResults));
        ObservingTransportAdapter sut = new(inner);
        List<OutboundMessage> seen = [];
        sut.MessageSent += seen.Add;

        IReadOnlyList<SendResult> results = await sut.SendBatchAsync([first, second], TestContext.Current.CancellationToken);

        seen.Should().Equal(first, second);
        results.Should().Equal(innerResults);
    }

    [Fact]
    public async Task ScheduleAsync_DelegatesToInner()
    {
        ITransportAdapter inner = InMemoryShapedInner();
        ScheduledMessageToken token = new(SequenceNumber: 42, Destination: "dest");
        ((INativeMessageScheduler)inner)
            .ScheduleAsync(Arg.Any<OutboundMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(token));
        ObservingTransportAdapter sut = new(inner);
        OutboundMessage message = CreateOutbound("a");

        ScheduledMessageToken result = await sut.ScheduleAsync(
            message, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        result.Should().Be(token);
        await ((INativeMessageScheduler)inner).Received(1)
            .ScheduleAsync(message, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_DisposesInner()
    {
        ITransportAdapter inner = InMemoryShapedInner();
        ObservingTransportAdapter sut = new(inner);

        await sut.DisposeAsync();

        await ((IAsyncDisposable)inner).Received(1).DisposeAsync();
    }
}
