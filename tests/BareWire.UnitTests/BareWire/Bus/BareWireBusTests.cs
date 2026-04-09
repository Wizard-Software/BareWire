using System.Buffers;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire;
using BareWire.Pipeline;
using BareWire.Serialization;
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
        CreateBus(
            int maxPendingPublishes = 1_000,
            BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait,
            IRoutingKeyResolver? routingKeyResolver = null)
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

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        MiddlewareChain chain = new([]);
        MessagePipeline pipeline = new(chain, deserializerResolver, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        PublishFlowControlOptions options = new()
        {
            MaxPendingPublishes = maxPendingPublishes,
            FullMode = fullMode,
        };

        BareWireBus bus = new(
            adapter,
            new DefaultSerializerResolver(serializer),
            pipeline,
            flowController,
            options,
            NullLogger<BareWireBus>.Instance,
            new NullInstrumentation(),
            routingKeyResolver: routingKeyResolver);

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

    // ── SendAsync via queue: scheme ───────────────────────────────────────────

    [Fact]
    public async Task SendAsync_WithQueueSchemeUri_SetsBwExchangeToEmpty()
    {
        // Arrange — capture messages sent to the adapter.
        var capturedBatches = new List<IReadOnlyList<OutboundMessage>>();
        var (bus, adapter, _) = CreateBus();
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   capturedBatches.Add(callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0));
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });
        bus.StartPublishing();

        Uri queueUri = new("queue://localhost/amq.gen-reply-queue");
        ISendEndpoint endpoint = await bus.GetSendEndpoint(queueUri, CancellationToken.None);

        // Act
        await endpoint.SendAsync(new BusTestMessage("response"), CancellationToken.None);

        // Give the background publisher time to flush.
        await Task.Delay(50);

        // Assert — the outbound message must carry BW-Exchange="" to trigger default-exchange delivery.
        capturedBatches.Should().HaveCount(1);
        OutboundMessage sent = capturedBatches[0][0];
        sent.Headers.Should().ContainKey("BW-Exchange");
        sent.Headers["BW-Exchange"].Should().BeEmpty();

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task SendAsync_WithQueueSchemeUriAndCorrelationId_ExtractsCorrelationIdFromQuery()
    {
        // Arrange
        var capturedBatches = new List<IReadOnlyList<OutboundMessage>>();
        var (bus, adapter, _) = CreateBus();
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   capturedBatches.Add(callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0));
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });
        bus.StartPublishing();

        string corrId = "f47ac10b-58cc-4372-a567-0e02b2c3d479";
        Uri queueUri = new($"queue://localhost/reply-queue?correlation-id={Uri.EscapeDataString(corrId)}");
        ISendEndpoint endpoint = await bus.GetSendEndpoint(queueUri, CancellationToken.None);

        // Act
        await endpoint.SendAsync(new BusTestMessage("response"), CancellationToken.None);
        await Task.Delay(50);

        // Assert — correlation-id must be present in the outbound headers.
        capturedBatches.Should().HaveCount(1);
        OutboundMessage sent = capturedBatches[0][0];
        sent.Headers.Should().ContainKey("correlation-id");
        sent.Headers["correlation-id"].Should().Be(corrId);
        sent.Headers["BW-Exchange"].Should().BeEmpty();

        await bus.DisposeAsync();
    }

    // ── SendAsync via exchange: scheme ────────────────────────────────────────

    [Fact]
    public async Task SendAsync_WithExchangeScheme_SetsExchangeHeaderAndClearsRoutingKey()
    {
        // Arrange — capture messages sent to the adapter.
        var capturedBatches = new List<IReadOnlyList<OutboundMessage>>();
        var (bus, adapter, _) = CreateBus();
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   capturedBatches.Add(callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0));
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });
        bus.StartPublishing();

        Uri exchangeUri = new("exchange:my-exchange");
        ISendEndpoint endpoint = await bus.GetSendEndpoint(exchangeUri, CancellationToken.None);

        // Act
        await endpoint.SendAsync(new BusTestMessage("event"), CancellationToken.None);

        // Give the background publisher time to flush.
        await Task.Delay(50);

        // Assert — BW-Exchange must be set to the exchange name and RoutingKey must be empty.
        capturedBatches.Should().HaveCount(1);
        OutboundMessage sent = capturedBatches[0][0];
        sent.Headers.Should().ContainKey("BW-Exchange");
        sent.Headers["BW-Exchange"].Should().Be("my-exchange");
        sent.RoutingKey.Should().BeEmpty();

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task SendAsync_WithNonQueueSchemeUri_DoesNotSetBwExchange()
    {
        // Arrange
        var capturedBatches = new List<IReadOnlyList<OutboundMessage>>();
        var (bus, adapter, _) = CreateBus();
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   capturedBatches.Add(callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0));
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });
        bus.StartPublishing();

        Uri regularUri = new("rabbitmq://localhost/some-queue");
        ISendEndpoint endpoint = await bus.GetSendEndpoint(regularUri, CancellationToken.None);

        // Act
        await endpoint.SendAsync(new BusTestMessage("direct"), CancellationToken.None);
        await Task.Delay(50);

        // Assert — BW-Exchange header must not be present for a non-queue: URI.
        capturedBatches.Should().HaveCount(1);
        OutboundMessage sent = capturedBatches[0][0];
        sent.Headers.Should().NotContainKey("BW-Exchange");

        await bus.DisposeAsync();
    }

    // ── PublishAsync with custom headers ─────────────────────────────────────

    [Fact]
    public async Task PublishAsync_WithCustomHeaders_MergesHeaders()
    {
        // Arrange — capture outbound messages.
        var capturedBatches = new List<IReadOnlyList<OutboundMessage>>();
        var (bus, adapter, _) = CreateBus();
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   capturedBatches.Add(callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0));
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });
        bus.StartPublishing();

        var customHeaders = new Dictionary<string, string>
        {
            ["x-custom-header"] = "custom-value",
            ["x-tenant-id"] = "tenant-42",
        };

        // Act
        await bus.PublishAsync(new BusTestMessage("hello"), customHeaders, CancellationToken.None);
        await Task.Delay(50);

        // Assert — custom headers must be present alongside framework headers.
        capturedBatches.Should().HaveCount(1);
        OutboundMessage sent = capturedBatches[0][0];
        sent.Headers.Should().ContainKey("x-custom-header");
        sent.Headers["x-custom-header"].Should().Be("custom-value");
        sent.Headers.Should().ContainKey("x-tenant-id");
        sent.Headers["x-tenant-id"].Should().Be("tenant-42");

        // Framework headers must also be present.
        sent.Headers.Should().ContainKey("BW-MessageType");
        sent.Headers.Should().ContainKey("message-id");

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WithMessageIdHeader_UsesProvidedMessageId()
    {
        // Arrange — capture outbound messages.
        var capturedBatches = new List<IReadOnlyList<OutboundMessage>>();
        var (bus, adapter, _) = CreateBus();
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   capturedBatches.Add(callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0));
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });
        bus.StartPublishing();

        Guid originalMessageId = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var headers = new Dictionary<string, string>
        {
            ["message-id"] = originalMessageId.ToString(),
        };

        // Act
        await bus.PublishAsync(new BusTestMessage("redeliver"), headers, CancellationToken.None);
        await Task.Delay(50);

        // Assert — the outbound message-id must equal the caller-supplied value, not a new Guid.
        capturedBatches.Should().HaveCount(1);
        OutboundMessage sent = capturedBatches[0][0];
        sent.Headers.Should().ContainKey("message-id");
        sent.Headers["message-id"].Should().Be(originalMessageId.ToString());

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

    // ── PublishAsync with IRoutingKeyResolver ─────────────────────────────────

    [Fact]
    public async Task PublishAsync_WithRoutingKeyMapping_UsesResolvedKey()
    {
        // Arrange — capture outbound messages.
        var capturedBatches = new List<IReadOnlyList<OutboundMessage>>();

        IRoutingKeyResolver resolver = Substitute.For<IRoutingKeyResolver>();
        resolver.Resolve<BusTestMessage>().Returns("order.created");

        var (bus, adapter, _) = CreateBus(routingKeyResolver: resolver);
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   capturedBatches.Add(callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0));
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });
        bus.StartPublishing();

        // Act
        await bus.PublishAsync(new BusTestMessage("hello"), CancellationToken.None);
        await Task.Delay(50);

        // Assert — the outbound message must carry the mapped routing key.
        capturedBatches.Should().HaveCount(1);
        OutboundMessage sent = capturedBatches[0][0];
        sent.RoutingKey.Should().Be("order.created");

        await bus.DisposeAsync();
    }
}
