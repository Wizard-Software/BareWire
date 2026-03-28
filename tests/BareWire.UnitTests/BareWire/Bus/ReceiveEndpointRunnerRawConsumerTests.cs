using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// Must be public so GetRequiredService<RawDispatchTestConsumer>() can resolve it via NSubstitute.
public sealed class RawDispatchTestConsumer : IRawConsumer
{
    private int _callCount;

    /// <summary>Number of times <see cref="ConsumeAsync"/> was called.</summary>
    public int CallCount => _callCount;

    public Task ConsumeAsync(RawConsumeContext context)
    {
        Interlocked.Increment(ref _callCount);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Regression tests for Bug 2: raw consumers registered via <c>RawConsumer&lt;T&gt;()</c> must be
/// dispatched by <see cref="ReceiveEndpointRunner"/> when no typed consumer matches the message.
/// </summary>
public sealed class ReceiveEndpointRunnerRawConsumerTests
{
    private const string EndpointName = "raw-test-endpoint";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (
        ReceiveEndpointRunner Runner,
        RawDispatchTestConsumer RawConsumer,
        ChannelWriter<InboundMessage> MessageWriter,
        ITransportAdapter Adapter)
        CreateRunner(bool includeTypedConsumer = false)
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

        // Deserializer that throws for typed consumers (simulates an unknown payload type).
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");
        // Returning null from Deserialize causes the typed invoker to throw UnknownPayloadException.
        deserializer.Deserialize<RunnerTestMessage>(Arg.Any<ReadOnlySequence<byte>>())
                    .Returns((RunnerTestMessage?)null);
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(deserializer);

        RawDispatchTestConsumer rawConsumer = new();

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(RawDispatchTestConsumer)).Returns(rawConsumer);
        provider.GetService(typeof(IEnumerable<IMessageMiddleware>))
            .Returns(Array.Empty<IMessageMiddleware>());

        if (includeTypedConsumer)
        {
            // Typed consumer also registered — typed consumer should never receive the message
            // because the deserializer returns null (UnknownPayloadException), so raw consumer wins.
            provider.GetService(typeof(RunnerTestConsumer)).Returns(new RunnerTestConsumer());
        }

        FlowControlOptions flowOptions = new() { MaxInFlightMessages = 4 };
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        List<ConsumerRegistration> typedConsumers = includeTypedConsumer
            ? [new ConsumerRegistration(typeof(RunnerTestConsumer), typeof(RunnerTestMessage))]
            : [];

        EndpointBinding binding = new()
        {
            EndpointName = EndpointName,
            PrefetchCount = 4,
            Consumers = typedConsumers,
            RawConsumers = [typeof(RawDispatchTestConsumer)],
        };

        IPublishEndpoint publishEndpoint = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();

        ReceiveEndpointRunner runner = new(
            binding,
            adapter,
            deserializerResolver,
            publishEndpoint,
            sendEndpointProvider,
            scopeFactory,
            flowController,
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        return (runner, rawConsumer, channel.Writer, adapter);
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
    public async Task RunAsync_WithOnlyRawConsumerRegistered_DispatchesToRawConsumer()
    {
        // Arrange
        var (runner, rawConsumer, writer, adapter) = CreateRunner(includeTypedConsumer: false);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(MakeMessage("msg-raw-1"), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — the raw consumer must have been called exactly once.
        rawConsumer.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WithRawConsumerRegistered_AcksMessage()
    {
        // Arrange
        var (runner, _, writer, adapter) = CreateRunner(includeTypedConsumer: false);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(MakeMessage("msg-ack-raw"), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — message must be ACK'd (raw consumer succeeded).
        await adapter.Received(1).SettleAsync(
            SettlementAction.Ack,
            Arg.Is<InboundMessage>(m => m.MessageId == "msg-ack-raw"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenTypedConsumerFailsToDeserialize_FallsBackToRawConsumer()
    {
        // Arrange — typed consumer is registered but deserializer returns null,
        // so typed invoker throws UnknownPayloadException and the raw consumer becomes the fallback.
        var (runner, rawConsumer, writer, adapter) = CreateRunner(includeTypedConsumer: true);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(MakeMessage("msg-fallback"), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — raw consumer must still be called, and the message must be ACK'd.
        rawConsumer.CallCount.Should().Be(1);
        await adapter.Received(1).SettleAsync(
            SettlementAction.Ack,
            Arg.Is<InboundMessage>(m => m.MessageId == "msg-fallback"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WithNoConsumersAtAll_RejectsMessage()
    {
        // Arrange — no consumers registered at all.
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
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(deserializer);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IEnumerable<IMessageMiddleware>))
            .Returns(Array.Empty<IMessageMiddleware>());

        FlowController flowController = new(NullLogger<FlowController>.Instance);

        EndpointBinding binding = new()
        {
            EndpointName = EndpointName,
            PrefetchCount = 4,
            Consumers = [],
            RawConsumers = [],
        };

        ReceiveEndpointRunner runner = new(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            scopeFactory,
            flowController,
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await channel.Writer.WriteAsync(MakeMessage("msg-reject"), cts.Token);
        channel.Writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — with no consumers, the message must be Rejected.
        await adapter.Received(1).SettleAsync(
            SettlementAction.Reject,
            Arg.Is<InboundMessage>(m => m.MessageId == "msg-reject"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_MultipleMessages_DispatchesRawConsumerForEach()
    {
        // Arrange
        var (runner, rawConsumer, writer, _) = CreateRunner(includeTypedConsumer: false);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        const int messageCount = 5;
        for (int i = 0; i < messageCount; i++)
        {
            await writer.WriteAsync(MakeMessage($"msg-{i}"), cts.Token);
        }
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — raw consumer must have been called once per message.
        rawConsumer.CallCount.Should().Be(messageCount);
    }
}
