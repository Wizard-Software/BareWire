using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
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

public sealed class BareWireBus_PublishSerializerOverrideTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (BareWireBus Bus, ITransportAdapter Adapter, List<OutboundMessage> Captured)
        CreateBus(ISerializerResolver serializerResolver)
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        List<OutboundMessage> captured = [];

        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   captured.AddRange(messages);
                   return Task.FromResult<IReadOnlyList<SendResult>>(
                       messages.Select(static _ => new SendResult(true, 0UL)).ToList());
               });

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();
        MiddlewareChain chain = new([]);
        MessagePipeline pipeline = new(chain, deserializerResolver, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        BareWireBus bus = new(
            adapter,
            serializerResolver,
            pipeline,
            flowController,
            new PublishFlowControlOptions(),
            NullLogger<BareWireBus>.Instance,
            new NullInstrumentation());

        return (bus, adapter, captured);
    }

    private static IMessageSerializer MakeSerializer(string contentType)
    {
        IMessageSerializer s = Substitute.For<IMessageSerializer>();
        s.ContentType.Returns(contentType);
        return s;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_WhenTypeMapped_UsesMappedSerializer()
    {
        // Arrange
        IMessageSerializer defaultSerializer = MakeSerializer("application/json");
        IMessageSerializer mappedSerializer = MakeSerializer("application/vnd.masstransit+json");

        var mappings = new Dictionary<Type, IMessageSerializer>
        {
            [typeof(OrderCreated)] = mappedSerializer,
        };

        TypeMappedSerializerResolver resolver = new(defaultSerializer, mappings);
        var (bus, _, captured) = CreateBus(resolver);
        bus.StartPublishing();

        // Act
        await bus.PublishAsync(new OrderCreated("ORD-1"), CancellationToken.None);
        await Task.Delay(50); // allow background publish loop to flush

        // Assert — serializer for mapped type was called
        mappedSerializer.Received(1).Serialize(Arg.Any<OrderCreated>(), Arg.Any<IBufferWriter<byte>>());
        defaultSerializer.DidNotReceiveWithAnyArgs().Serialize<OrderCreated>(default!, default!);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WhenTypeNotMapped_UsesDefaultSerializer()
    {
        // Arrange
        IMessageSerializer defaultSerializer = MakeSerializer("application/json");
        IMessageSerializer mappedSerializer = MakeSerializer("application/vnd.masstransit+json");

        var mappings = new Dictionary<Type, IMessageSerializer>
        {
            [typeof(OrderCreated)] = mappedSerializer,
        };

        TypeMappedSerializerResolver resolver = new(defaultSerializer, mappings);
        var (bus, _, _) = CreateBus(resolver);
        bus.StartPublishing();

        // Act
        await bus.PublishAsync(new PaymentRequested("PAY-1"), CancellationToken.None);
        await Task.Delay(50);

        // Assert — default serializer used for unmapped type
        defaultSerializer.Received(1).Serialize(Arg.Any<PaymentRequested>(), Arg.Any<IBufferWriter<byte>>());
        mappedSerializer.DidNotReceiveWithAnyArgs().Serialize<PaymentRequested>(default!, default!);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WhenMappedForTypeA_DoesNotAffectTypeB()
    {
        // Arrange — key ADR-001 validation: per-type override must not bleed into other types
        IMessageSerializer defaultSerializer = MakeSerializer("application/json");
        IMessageSerializer envelopeSerializer = MakeSerializer("application/vnd.masstransit+json");

        var mappings = new Dictionary<Type, IMessageSerializer>
        {
            [typeof(OrderCreated)] = envelopeSerializer,
        };

        TypeMappedSerializerResolver resolver = new(defaultSerializer, mappings);
        var (bus, _, _) = CreateBus(resolver);
        bus.StartPublishing();

        // Act — publish both message types
        await bus.PublishAsync(new OrderCreated("ORD-1"), CancellationToken.None);
        await bus.PublishAsync(new PaymentRequested("PAY-1"), CancellationToken.None);
        await Task.Delay(80);

        // Assert — each type uses its own serializer
        envelopeSerializer.Received(1).Serialize(Arg.Any<OrderCreated>(), Arg.Any<IBufferWriter<byte>>());
        defaultSerializer.Received(1).Serialize(Arg.Any<PaymentRequested>(), Arg.Any<IBufferWriter<byte>>());

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task SendAsync_WhenTypeMapped_UsesMappedSerializer()
    {
        // Arrange — verifies BareWireSendEndpoint also honours per-type mappings
        IMessageSerializer defaultSerializer = MakeSerializer("application/json");
        IMessageSerializer envelopeSerializer = MakeSerializer("application/vnd.masstransit+json");

        var mappings = new Dictionary<Type, IMessageSerializer>
        {
            [typeof(OrderCreated)] = envelopeSerializer,
        };

        TypeMappedSerializerResolver resolver = new(defaultSerializer, mappings);
        var (bus, _, _) = CreateBus(resolver);
        bus.StartPublishing();

        ISendEndpoint endpoint = await bus.GetSendEndpoint(new Uri("exchange://mt-orders/OrderCreated"));

        // Act
        await endpoint.SendAsync(new OrderCreated("ORD-2"), CancellationToken.None);
        await Task.Delay(50);

        // Assert
        envelopeSerializer.Received(1).Serialize(Arg.Any<OrderCreated>(), Arg.Any<IBufferWriter<byte>>());

        await bus.DisposeAsync();
    }

    // ── Message types ──────────────────────────────────────────────────────────

    private sealed record OrderCreated(string OrderId);
    private sealed record PaymentRequested(string PaymentId);
}
