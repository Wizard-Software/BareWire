using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Core.Observability;
using BareWire.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class MessagePipelineTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static InboundMessage CreateInboundMessage(string messageId = "test-id")
        => new(
            messageId: messageId,
            headers: new Dictionary<string, string>(),
            body: ReadOnlySequence<byte>.Empty,
            deliveryTag: 1UL);

    private static (
        MessagePipeline Pipeline,
        MiddlewareChain Chain,
        ITransportAdapter Adapter,
        IServiceProvider ServiceProvider)
        CreatePipeline(IReadOnlyList<IMessageMiddleware>? middlewares = null)
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.SettleAsync(Arg.Any<SettlementAction>(), Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();

        var chain = new MiddlewareChain(middlewares ?? []);
        var dispatcher = new ConsumerDispatcher(scopeFactory, NullLogger<ConsumerDispatcher>.Instance);
        var pipeline = new MessagePipeline(chain, dispatcher, deserializer, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());

        return (pipeline, chain, adapter, serviceProvider);
    }

    // ── Inbound tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessInboundAsync_SuccessfulConsume_ReturnsAck()
    {
        var (pipeline, _, adapter, serviceProvider) = CreatePipeline();
        InboundMessage message = CreateInboundMessage();

        SettlementAction result = await pipeline.ProcessInboundAsync(message, adapter, serviceProvider, CancellationToken.None);

        result.Should().Be(SettlementAction.Ack);
        await adapter.Received(1).SettleAsync(SettlementAction.Ack, message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessInboundAsync_ConsumerThrows_ReturnsNack()
    {
        IMessageMiddleware throwingMiddleware = Substitute.For<IMessageMiddleware>();
        throwingMiddleware.InvokeAsync(Arg.Any<MessageContext>(), Arg.Any<NextMiddleware>())
            .Returns<Task>(_ => throw new InvalidOperationException("consumer failed"));

        var (pipeline, _, adapter, serviceProvider) = CreatePipeline([throwingMiddleware]);
        InboundMessage message = CreateInboundMessage();

        SettlementAction result = await pipeline.ProcessInboundAsync(message, adapter, serviceProvider, CancellationToken.None);

        result.Should().Be(SettlementAction.Nack);
        await adapter.Received(1).SettleAsync(SettlementAction.Nack, message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessInboundAsync_UnknownPayload_ReturnsReject()
    {
        IMessageMiddleware throwingMiddleware = Substitute.For<IMessageMiddleware>();
        throwingMiddleware.InvokeAsync(Arg.Any<MessageContext>(), Arg.Any<NextMiddleware>())
            .Returns<Task>(_ => throw new UnknownPayloadException("test-endpoint", "application/unknown"));

        var (pipeline, _, adapter, serviceProvider) = CreatePipeline([throwingMiddleware]);
        InboundMessage message = CreateInboundMessage();

        SettlementAction result = await pipeline.ProcessInboundAsync(message, adapter, serviceProvider, CancellationToken.None);

        result.Should().Be(SettlementAction.Reject);
        await adapter.Received(1).SettleAsync(SettlementAction.Reject, message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessInboundAsync_ExecutesMiddlewareChain()
    {
        var executionOrder = new List<string>();

        IMessageMiddleware first = Substitute.For<IMessageMiddleware>();
        first.InvokeAsync(Arg.Any<MessageContext>(), Arg.Any<NextMiddleware>())
            .Returns(async callInfo =>
            {
                executionOrder.Add("first");
                await callInfo.ArgAt<NextMiddleware>(1)(callInfo.ArgAt<MessageContext>(0));
            });

        IMessageMiddleware second = Substitute.For<IMessageMiddleware>();
        second.InvokeAsync(Arg.Any<MessageContext>(), Arg.Any<NextMiddleware>())
            .Returns(async callInfo =>
            {
                executionOrder.Add("second");
                await callInfo.ArgAt<NextMiddleware>(1)(callInfo.ArgAt<MessageContext>(0));
            });

        var (pipeline, _, adapter, serviceProvider) = CreatePipeline([first, second]);
        InboundMessage message = CreateInboundMessage();

        await pipeline.ProcessInboundAsync(message, adapter, serviceProvider, CancellationToken.None);

        executionOrder.Should().Equal("first", "second");
    }

    // ── Outbound tests ───────────────────────────────────────────────────────

    [Fact]
    public void ProcessOutboundAsync_SerializesAndSends()
    {
        var (pipeline, _, _, _) = CreatePipeline();

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer.When(s => s.Serialize(Arg.Any<TestMessage>(), Arg.Any<IBufferWriter<byte>>()))
                  .Do(_ => { /* no-op: zero bytes written is fine for this test */ });

        TestMessage message = new("hello");
        const string routingKey = "my.routing.key";
        var extraHeaders = new Dictionary<string, string> { ["x-trace"] = "abc" };

        OutboundMessage outbound = MessagePipeline.ProcessOutboundAsync(message, serializer, routingKey, extraHeaders, CancellationToken.None);

        outbound.RoutingKey.Should().Be(routingKey);
        outbound.ContentType.Should().Be("application/json");
        outbound.Headers.Should().ContainKey("x-trace").WhoseValue.Should().Be("abc");
    }

    [Fact]
    public void ProcessOutboundAsync_UsesPooledBufferWriter()
    {
        var (pipeline, _, _, _) = CreatePipeline();

        IBufferWriter<byte>? capturedWriter = null;
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer.When(s => s.Serialize(Arg.Any<TestMessage>(), Arg.Any<IBufferWriter<byte>>()))
                  .Do(callInfo => capturedWriter = callInfo.ArgAt<IBufferWriter<byte>>(1));

        MessagePipeline.ProcessOutboundAsync(new TestMessage("value"), serializer, "key", null, CancellationToken.None);

        capturedWriter.Should().NotBeNull();
        capturedWriter.Should().BeAssignableTo<IBufferWriter<byte>>();
    }
}
