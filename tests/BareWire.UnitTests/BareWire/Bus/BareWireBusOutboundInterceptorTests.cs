using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

public sealed class BareWireBusOutboundInterceptorTests
{
    private sealed record TestEvent(string Id);

    private sealed class CapturingInterceptor(bool capture, bool isCapturing = true) : IOutboundMessageInterceptor
    {
        public List<OutboundMessage> Seen { get; } = [];

        public bool IsCapturing => isCapturing;

        public bool TryIntercept(OutboundMessage message)
        {
            Seen.Add(message);
            return capture;
        }
    }

    private sealed class RecordingLogger : ILogger<BareWireBus>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (BareWireBus Bus, ITransportAdapter Adapter) CreateBus(
        IOutboundMessageInterceptor? interceptor,
        ILogger<BareWireBus>? logger = null,
        IBareWireInstrumentation? instrumentation = null)
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<SendResult>>(
                call.ArgAt<IReadOnlyList<OutboundMessage>>(0).Select(static _ => new SendResult(true, 0UL)).ToList()));

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        ISerializerResolver serializerResolver = Substitute.For<ISerializerResolver>();
        serializerResolver.Resolve<TestEvent>().Returns(serializer);

        instrumentation ??= new NullInstrumentation();
        MessagePipeline pipeline = new(
            new MiddlewareChain([]),
            Substitute.For<IDeserializerResolver>(),
            NullLogger<MessagePipeline>.Instance,
            instrumentation);

        BareWireBus bus = new(
            adapter,
            serializerResolver,
            pipeline,
            new FlowController(NullLogger<FlowController>.Instance),
            new PublishFlowControlOptions(),
            logger ?? NullLogger<BareWireBus>.Instance,
            instrumentation,
            outboundInterceptor: interceptor);

        bus.StartPublishing();
        return (bus, adapter);
    }

    [Fact]
    public async Task PublishAsync_WhenInterceptorCaptures_DoesNotSendToTransport()
    {
        CapturingInterceptor interceptor = new(capture: true);
        var (bus, adapter) = CreateBus(interceptor);

        await bus.PublishAsync(new TestEvent("1"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        interceptor.Seen.Should().ContainSingle();
        interceptor.Seen[0].Headers["BW-MessageType"].Should().Be(nameof(TestEvent));
        await adapter.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WhenInterceptorDeclines_SendsToTransport()
    {
        CapturingInterceptor interceptor = new(capture: false);
        var (bus, adapter) = CreateBus(interceptor);

        await bus.PublishAsync(new TestEvent("1"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        interceptor.Seen.Should().ContainSingle();
        await adapter.ReceivedWithAnyArgs().SendBatchAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WhenNoInterceptorRegistered_SendsToTransport()
    {
        var (bus, adapter) = CreateBus(interceptor: null);

        await bus.PublishAsync(new TestEvent("1"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await adapter.ReceivedWithAnyArgs().SendBatchAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishRawAsync_WhenInterceptorCaptures_DoesNotSendToTransport()
    {
        CapturingInterceptor interceptor = new(capture: true);
        var (bus, adapter) = CreateBus(interceptor);

        await bus.PublishRawAsync(new byte[] { 1, 2, 3 }, "application/octet-stream",
            TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        interceptor.Seen.Should().ContainSingle();
        await adapter.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishRawAsync_WhenInterceptorCaptures_BodyIsOwnedCopyUnaffectedByLaterMutation()
    {
        CapturingInterceptor interceptor = new(capture: true);
        var (bus, _) = CreateBus(interceptor);
        byte[] source = [1, 2, 3, 4];

        await bus.PublishRawAsync(source, "application/octet-stream", TestContext.Current.CancellationToken);
        Array.Fill(source, (byte)9); // simulates the caller returning/reusing an ArrayPool buffer

        interceptor.Seen[0].Body.ToArray().Should().Equal(1, 2, 3, 4);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishRawAsync_WhenNoInterceptor_SendsPayloadWithoutCopy()
    {
        ITransportAdapter? adapter = null;
        List<OutboundMessage> sent = [];
        (BareWireBus bus, adapter) = CreateBus(interceptor: null);
        adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyList<OutboundMessage> batch = call.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                sent.AddRange(batch);
                return Task.FromResult<IReadOnlyList<SendResult>>(
                    batch.Select(static _ => new SendResult(true, 0UL)).ToList());
            });
        byte[] source = [1, 2, 3];

        await bus.PublishRawAsync(source, "application/octet-stream", TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        sent.Should().ContainSingle();
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(sent[0].Body, out ArraySegment<byte> segment)
            .Should().BeTrue();
        segment.Array.Should().BeSameAs(source);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WhenInterceptorNotCapturing_DoesNotCallTryIntercept()
    {
        CapturingInterceptor interceptor = new(capture: true, isCapturing: false);
        var (bus, adapter) = CreateBus(interceptor);

        await bus.PublishAsync(new TestEvent("1"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        interceptor.Seen.Should().BeEmpty();
        await adapter.ReceivedWithAnyArgs().SendBatchAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishRawAsync_WhenInterceptorNotCapturing_SkipsInterceptionAndSendsOriginalMemory()
    {
        CapturingInterceptor interceptor = new(capture: true, isCapturing: false);
        List<OutboundMessage> sent = [];
        (BareWireBus bus, ITransportAdapter adapter) = CreateBus(interceptor);
        CaptureSent(adapter, sent);
        byte[] source = [1, 2, 3];

        await bus.PublishRawAsync(source, "application/octet-stream", TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        interceptor.Seen.Should().BeEmpty();
        sent.Should().ContainSingle();
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(sent[0].Body, out ArraySegment<byte> segment)
            .Should().BeTrue();
        segment.Array.Should().BeSameAs(source);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task SendRawAsync_WhenInterceptorNotCapturing_SkipsInterceptionAndSendsOriginalMemory()
    {
        CapturingInterceptor interceptor = new(capture: true, isCapturing: false);
        List<OutboundMessage> sent = [];
        (BareWireBus bus, ITransportAdapter adapter) = CreateBus(interceptor);
        CaptureSent(adapter, sent);
        byte[] source = [4, 5, 6];

        ISendEndpoint endpoint = await bus.GetSendEndpoint(
            new Uri("queue:orders"), TestContext.Current.CancellationToken);
        await endpoint.SendRawAsync(source, "application/octet-stream", TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        interceptor.Seen.Should().BeEmpty();
        sent.Should().ContainSingle();
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(sent[0].Body, out ArraySegment<byte> segment)
            .Should().BeTrue();
        segment.Array.Should().BeSameAs(source);

        await bus.DisposeAsync();
    }

    private static void CaptureSent(ITransportAdapter adapter, List<OutboundMessage> sent)
        => adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyList<OutboundMessage> batch = call.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                sent.AddRange(batch);
                return Task.FromResult<IReadOnlyList<SendResult>>(
                    batch.Select(static _ => new SendResult(true, 0UL)).ToList());
            });

    [Fact]
    public async Task SendAsync_WhenInterceptorCaptures_DoesNotSendToTransport()
    {
        CapturingInterceptor interceptor = new(capture: true);
        var (bus, adapter) = CreateBus(interceptor);

        ISendEndpoint endpoint = await bus.GetSendEndpoint(
            new Uri("queue:orders"), TestContext.Current.CancellationToken);
        await endpoint.SendAsync(new TestEvent("1"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        interceptor.Seen.Should().ContainSingle();
        interceptor.Seen[0].RoutingKey.Should().Be("orders");
        await adapter.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task SendAsync_WhenInterceptorDeclines_SendsToTransport()
    {
        CapturingInterceptor interceptor = new(capture: false);
        var (bus, adapter) = CreateBus(interceptor);

        ISendEndpoint endpoint = await bus.GetSendEndpoint(
            new Uri("queue:orders"), TestContext.Current.CancellationToken);
        await endpoint.SendAsync(new TestEvent("1"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        await adapter.ReceivedWithAnyArgs().SendBatchAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task SendRawAsync_WhenInterceptorCaptures_BodyIsOwnedCopyAndNotSentToTransport()
    {
        CapturingInterceptor interceptor = new(capture: true);
        var (bus, adapter) = CreateBus(interceptor);
        byte[] source = [5, 6, 7];

        ISendEndpoint endpoint = await bus.GetSendEndpoint(
            new Uri("queue:orders"), TestContext.Current.CancellationToken);
        await endpoint.SendRawAsync(source, "application/octet-stream", TestContext.Current.CancellationToken);
        Array.Fill(source, (byte)0);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        interceptor.Seen.Should().ContainSingle();
        interceptor.Seen[0].Body.ToArray().Should().Equal(5, 6, 7);
        await adapter.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WhenInterceptorCaptures_RecordsInterceptedMetricInsteadOfPublish()
    {
        IBareWireInstrumentation instrumentation = Substitute.For<IBareWireInstrumentation>();
        var (bus, _) = CreateBus(new CapturingInterceptor(capture: true), instrumentation: instrumentation);

        await bus.PublishAsync(new TestEvent("1"), TestContext.Current.CancellationToken);

        instrumentation.Received(1).RecordPublishIntercepted(Arg.Any<string>(), nameof(TestEvent));
        instrumentation.DidNotReceiveWithAnyArgs().RecordPublish(default!, default!, default);
        instrumentation.DidNotReceiveWithAnyArgs().RecordPublishPending(default!, default);

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WhenInterceptorCaptures_LogsOnlyMessageIdAndTypeAtDebug()
    {
        RecordingLogger logger = new();
        Guid messageId = Guid.NewGuid();
        var (bus, _) = CreateBus(new CapturingInterceptor(capture: true), logger);

        await bus.PublishAsync(
            new TestEvent("secret-body-value"),
            new Dictionary<string, string> { ["message-id"] = messageId.ToString(), ["x-secret"] = "header-secret" },
            TestContext.Current.CancellationToken);

        (LogLevel Level, string Message) entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug).Subject;
        entry.Message.Should().Contain(messageId.ToString()).And.Contain(nameof(TestEvent));
        entry.Message.Should().NotContain("secret");

        await bus.DisposeAsync();
    }
}
