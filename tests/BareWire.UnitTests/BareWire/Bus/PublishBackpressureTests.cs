using System.Buffers;
using System.Threading.Channels;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

public sealed class PublishBackpressureTests
{
    private static (BareWireBus Bus, ITransportAdapter Adapter) CreateBus(
        int maxPendingPublishes = 1_000,
        long maxPendingBytes = 256 * 1024 * 1024,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait,
        ILogger<BareWireBus>? logger = null)
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
                  .Do(_ => { });

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        MiddlewareChain chain = new([]);
        MessagePipeline pipeline = new(chain, deserializerResolver, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        PublishFlowControlOptions options = new()
        {
            MaxPendingPublishes = maxPendingPublishes,
            MaxPendingBytes = maxPendingBytes,
            FullMode = fullMode,
        };

        BareWireBus bus = new(
            adapter,
            new DefaultSerializerResolver(serializer),
            pipeline,
            flowController,
            options,
            logger ?? NullLogger<BareWireBus>.Instance,
            new NullInstrumentation());

        return (bus, adapter);
    }

    [Fact]
    public async Task PublishAsync_BelowLimit_CompletesImmediately()
    {
        // Arrange
        var (bus, _) = CreateBus(maxPendingBytes: 1024 * 1024);
        bus.StartPublishing();

        // Act + Assert
        Func<Task> act = () => bus.PublishAsync(new BusTestMessage("hello"));
        await act.Should().NotThrowAsync();

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_ByteLimitExceeded_ActivatesBackpressure()
    {
        // Arrange: 1-byte limit, Wait mode. 10-byte payload exceeds limit → blocks → cancelled.
        var (bus, _) = CreateBus(maxPendingBytes: 1, fullMode: BoundedChannelFullMode.Wait);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        byte[] payload = new byte[10];

        Func<Task> blockedCall = () => bus.PublishRawAsync(payload, "application/octet-stream", cts.Token);

        await blockedCall.Should().ThrowAsync<OperationCanceledException>();

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_DropWriteMode_DropsMessageWhenFull()
    {
        // Arrange: channel capacity 2, DropWrite mode. Publisher NOT started.
        var (bus, adapter) = CreateBus(
            maxPendingPublishes: 2,
            fullMode: BoundedChannelFullMode.DropWrite);

        await bus.PublishAsync(new BusTestMessage("1"));
        await bus.PublishAsync(new BusTestMessage("2"));

        // Third write: channel full, DropWrite silently discards.
        Func<Task> thirdWrite = () => bus.PublishAsync(new BusTestMessage("3"));
        await thirdWrite.Should().NotThrowAsync();

        // Adapter not called (publisher loop not started).
        await adapter.DidNotReceive().SendBatchAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_ChannelFull_WaitMode_BlocksUntilSpace()
    {
        // Arrange: capacity 1, Wait mode. Publisher started so it drains.
        var (bus, _) = CreateBus(maxPendingPublishes: 1, fullMode: BoundedChannelFullMode.Wait);
        bus.StartPublishing();

        // First publish fills the single slot.
        await bus.PublishAsync(new BusTestMessage("first"));

        // Second publish: channel may be full momentarily but publisher loop drains it.
        // If backpressure works, this will eventually complete (not hang) because the
        // publisher loop frees up space. Use a timeout to catch deadlocks.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Func<Task> secondPublish = () => bus.PublishAsync(new BusTestMessage("second"), cts.Token);

        await secondPublish.Should().NotThrowAsync("publisher loop should drain and unblock");

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_At90Percent_LogsHealthAlert()
    {
        // Arrange: capacity 10, DropWrite mode so we can fill without blocking.
        var logSink = new CapturingLogger();

        var (bus, _) = CreateBus(
            maxPendingPublishes: 10,
            fullMode: BoundedChannelFullMode.DropWrite,
            logger: logSink);

        // Health check runs BEFORE WriteAsync, so count observed is N-1 when publishing Nth.
        // Fill 10 of 10 slots — on the 10th publish, count is 9/10 = 90% → alert logged.
        for (int i = 0; i < 10; i++)
            await bus.PublishAsync(new BusTestMessage($"msg-{i}"));

        logSink.WarningLogged.Should().BeTrue("health alert should be logged when count reaches 90%");

        await bus.DisposeAsync();
    }

    private sealed class CapturingLogger : ILogger<BareWireBus>
    {
        public bool WarningLogged { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                WarningLogged = true;
        }
    }
}
