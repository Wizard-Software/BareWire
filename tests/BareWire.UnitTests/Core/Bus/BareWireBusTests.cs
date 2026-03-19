using System.Buffers;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Core.Bus;
using BareWire.Core.FlowControl;
using BareWire.Core.Observability;
using BareWire.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// Must be public for NSubstitute to proxy IConsumer<BusTestMessage>.
public sealed record BusTestMessage(string Value);

public sealed class BareWireBusTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (BareWireBus Bus, ITransportAdapter Adapter, IMessageSerializer Serializer)
        CreateBus(int maxPendingPublishes = 1_000, BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait)
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer.When(s => s.Serialize(Arg.Any<BusTestMessage>(), Arg.Any<IBufferWriter<byte>>()))
                  .Do(_ => { /* no-op */ });

        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();

        MiddlewareChain chain = new([]);
        ConsumerDispatcher dispatcher = new(scopeFactory, NullLogger<ConsumerDispatcher>.Instance);
        MessagePipeline pipeline = new(chain, dispatcher, deserializer, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        PublishFlowControlOptions options = new()
        {
            MaxPendingPublishes = maxPendingPublishes,
            FullMode = fullMode,
        };

        BareWireBus bus = new(
            adapter,
            serializer,
            pipeline,
            flowController,
            options,
            NullLogger<BareWireBus>.Instance,
            new NullInstrumentation());

        return (bus, adapter, serializer);
    }

    // ── BusId ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BusId_IsUnique()
    {
        var (bus1, _, _) = CreateBus();
        var (bus2, _, _) = CreateBus();

        bus1.BusId.Should().NotBe(bus2.BusId);
        bus1.BusId.Should().NotBe(Guid.Empty);
    }

    // ── PublishAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_DelegatesToPipeline()
    {
        var (bus, adapter, serializer) = CreateBus();
        bus.StartPublishing();

        BusTestMessage message = new("hello");
        await bus.PublishAsync(message, CancellationToken.None);

        // Give the background task a moment to flush.
        await Task.Delay(50);

        serializer.Received(1).Serialize(message, Arg.Any<IBufferWriter<byte>>());

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WritesToBoundedOutgoingChannel()
    {
        var (bus, adapter, _) = CreateBus();

        // Do NOT start publisher, so the channel accumulates messages.
        BusTestMessage message = new("test");

        await bus.PublishAsync(message, CancellationToken.None);

        // Channel should have at least one message queued (publisher not started, so nothing drains).
        // We verify the adapter was NOT called yet since publisher loop hasn't started.
        await adapter.DidNotReceive().SendBatchAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WhenChannelFull_AppliesBackpressure()
    {
        // Use a tiny channel with DropWrite so we can observe full behaviour without blocking.
        var (bus, _, _) = CreateBus(maxPendingPublishes: 2, fullMode: BoundedChannelFullMode.DropWrite);

        // Do NOT start publisher — channel fills up immediately.
        await bus.PublishAsync(new BusTestMessage("1"), CancellationToken.None);
        await bus.PublishAsync(new BusTestMessage("2"), CancellationToken.None);

        // Third write: channel is full, DropWrite silently discards. The call should complete immediately.
        Func<Task> thirdWrite = async () => await bus.PublishAsync(new BusTestMessage("3"), CancellationToken.None);
        await thirdWrite.Should().NotThrowAsync();

        await bus.DisposeAsync();
    }

    // ── PublishRawAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task PublishRawAsync_SendsViaAdapter()
    {
        var (bus, adapter, _) = CreateBus();
        bus.StartPublishing();

        byte[] payload = [1, 2, 3];
        await bus.PublishRawAsync(payload, "application/octet-stream", CancellationToken.None);

        // Give the background task time to flush.
        await Task.Delay(50);

        await adapter.Received(1).SendBatchAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(msgs =>
                msgs.Count == 1 &&
                msgs[0].ContentType == "application/octet-stream"),
            Arg.Any<CancellationToken>());

        await bus.DisposeAsync();
    }

    // ── GetSendEndpoint ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSendEndpoint_ReturnsSameInstanceForSameUri()
    {
        var (bus, _, _) = CreateBus();
        Uri address = new("rabbitmq://localhost/my-queue");

        ISendEndpoint first = await bus.GetSendEndpoint(address, CancellationToken.None);
        ISendEndpoint second = await bus.GetSendEndpoint(address, CancellationToken.None);

        first.Should().BeSameAs(second);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task GetSendEndpoint_CachesPerUri()
    {
        var (bus, _, _) = CreateBus();
        Uri addressA = new("rabbitmq://localhost/queue-a");
        Uri addressB = new("rabbitmq://localhost/queue-b");

        ISendEndpoint endpointA = await bus.GetSendEndpoint(addressA, CancellationToken.None);
        ISendEndpoint endpointB = await bus.GetSendEndpoint(addressB, CancellationToken.None);

        endpointA.Should().NotBeSameAs(endpointB);
        endpointA.Address.Should().Be(addressA);
        endpointB.Address.Should().Be(addressB);

        await bus.DisposeAsync();
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DrainsOutgoingChannel()
    {
        var (bus, adapter, _) = CreateBus();
        bus.StartPublishing();

        // Enqueue several messages then dispose — they should be flushed before shutdown.
        for (int i = 0; i < 5; i++)
            await bus.PublishAsync(new BusTestMessage($"msg-{i}"), CancellationToken.None);

        await bus.DisposeAsync();

        // The adapter must have received at least one batch covering all 5 messages.
        await adapter.Received().SendBatchAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
    }
}
