using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Core.Bus;
using BareWire.Core.FlowControl;
using BareWire.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// Must be public so ConsumerInvokerFactory.CreateRaw can build a delegate over this type
// and GetService<ThrowingRawConsumer>() can resolve it from the NSubstitute DI mock.
public sealed class ThrowingRawConsumer : IRawConsumer
{
    private int _callCount;

    /// <summary>Number of times <see cref="ConsumeAsync"/> was called (including retry attempts).</summary>
    public int CallCount => _callCount;

    public Task ConsumeAsync(RawConsumeContext context)
    {
        Interlocked.Increment(ref _callCount);
        throw new InvalidOperationException("Consumer failed intentionally.");
    }
}

/// <summary>
/// Tests that verify retry middleware is correctly wired into <see cref="ReceiveEndpointRunner"/>
/// via <see cref="EndpointBinding.RetryCount"/> and <see cref="EndpointBinding.RetryInterval"/>.
/// </summary>
public sealed class ReceiveEndpointRunnerTests
{
    private const string EndpointName = "retry-test-endpoint";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (
        ReceiveEndpointRunner Runner,
        ThrowingRawConsumer Consumer,
        ChannelWriter<InboundMessage> MessageWriter,
        ITransportAdapter Adapter)
        CreateRunnerWithThrowingConsumer(int retryCount, TimeSpan retryInterval)
    {
        Channel<InboundMessage> channel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(64) { SingleWriter = false, SingleReader = true });

        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.ConsumeAsync(
                Arg.Any<string>(),
                Arg.Any<FlowControlOptions>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo => ReadChannelAsync(channel.Reader, callInfo.ArgAt<CancellationToken>(2)));
        adapter.SettleAsync(
                Arg.Any<SettlementAction>(),
                Arg.Any<InboundMessage>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");

        ThrowingRawConsumer consumer = new();

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(ThrowingRawConsumer)).Returns(consumer);

        FlowController flowController = new(NullLogger<FlowController>.Instance);

        EndpointBinding binding = new()
        {
            EndpointName = EndpointName,
            PrefetchCount = 4,
            Consumers = [],
            RawConsumers = [typeof(ThrowingRawConsumer)],
            RetryCount = retryCount,
            RetryInterval = retryInterval,
        };

        // Pass a real ILoggerFactory so RetryMiddleware and DeadLetterMiddleware get loggers.
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;

        ReceiveEndpointRunner runner = new(
            binding,
            adapter,
            deserializer,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            scopeFactory,
            flowController,
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance,
            loggerFactory: loggerFactory);

        return (runner, consumer, channel.Writer, adapter);
    }

    private static InboundMessage MakeMessage(string id = "msg-1")
    {
        byte[] body = new byte[8];
        return new InboundMessage(
            messageId: id,
            headers: new Dictionary<string, string>(),
            body: new ReadOnlySequence<byte>(body),
            deliveryTag: 1UL);
    }

    private static async IAsyncEnumerable<InboundMessage> ReadChannelAsync(
        ChannelReader<InboundMessage> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (InboundMessage msg in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return msg;
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ConsumerThrowsWithRetry_RetriesBeforeNack()
    {
        // Arrange — RetryCount=2 means 1 original attempt + 2 retry attempts = 3 total calls.
        var (runner, consumer, writer, adapter) = CreateRunnerWithThrowingConsumer(
            retryCount: 2,
            retryInterval: TimeSpan.Zero);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(MakeMessage("msg-retry"), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer was invoked 3 times (1 initial + 2 retries).
        consumer.CallCount.Should().Be(3);

        // After all retries are exhausted, the message must be NACKed.
        await adapter.Received(1).SettleAsync(
            SettlementAction.Nack,
            Arg.Is<InboundMessage>(m => m.MessageId == "msg-retry"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NoRetryConfigured_ImmediateNackOnException()
    {
        // Arrange — RetryCount=0 means no retry middleware is added; consumer is called once.
        var (runner, consumer, writer, adapter) = CreateRunnerWithThrowingConsumer(
            retryCount: 0,
            retryInterval: TimeSpan.Zero);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(MakeMessage("msg-no-retry"), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer was invoked exactly once (no retries).
        consumer.CallCount.Should().Be(1);

        // Immediate NACK — no retry delay.
        await adapter.Received(1).SettleAsync(
            SettlementAction.Nack,
            Arg.Is<InboundMessage>(m => m.MessageId == "msg-no-retry"),
            Arg.Any<CancellationToken>());
    }
}
