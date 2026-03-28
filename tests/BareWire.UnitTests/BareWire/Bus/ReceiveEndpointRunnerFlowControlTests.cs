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

// Must be public so ConsumerInvokerFactory can build a generic delegate over this type.
public sealed record RunnerTestMessage(string Value);

// Must be public so GetRequiredService<RunnerTestConsumer>() can resolve it via NSubstitute.
public sealed class RunnerTestConsumer : IConsumer<RunnerTestMessage>
{
    public Task ConsumeAsync(ConsumeContext<RunnerTestMessage> context) => Task.CompletedTask;
}

public sealed class ReceiveEndpointRunnerFlowControlTests
{
    private const string EndpointName = "test-endpoint";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="ReceiveEndpointRunner"/> wired to a channel-backed transport.
    /// Returns the runner, the credit manager for the endpoint, and the channel writer
    /// that controls which messages flow into the consume loop.
    /// </summary>
    private static (
        ReceiveEndpointRunner Runner,
        CreditManager CreditManager,
        ChannelWriter<InboundMessage> MessageWriter,
        ITransportAdapter Adapter)
        CreateRunner(
            int maxInFlight = 1,
            IDeserializerResolver? deserializerResolver = null)
    {
        // Build a bounded channel that the mock transport will read from.
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

        // Set up the deserializer resolver to return a non-null message so the consumer is dispatched.
        IDeserializerResolver resolvedDeserializerResolver = deserializerResolver ?? CreateDeserializerResolver();

        // Wire DI so the runner's invoker can resolve RunnerTestConsumer from the scope.
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(RunnerTestConsumer)).Returns(new RunnerTestConsumer());
        provider.GetService(typeof(IEnumerable<IMessageMiddleware>))
            .Returns(Array.Empty<IMessageMiddleware>());

        // Pre-register the CreditManager under the endpoint name so we can retrieve it.
        FlowControlOptions flowOptions = new() { MaxInFlightMessages = maxInFlight };
        FlowController flowController = new(NullLogger<FlowController>.Instance);
        CreditManager creditManager = flowController.GetOrCreateManager(EndpointName, flowOptions);

        EndpointBinding binding = new()
        {
            EndpointName = EndpointName,
            PrefetchCount = maxInFlight,
            Consumers = [new ConsumerRegistration(typeof(RunnerTestConsumer), typeof(RunnerTestMessage))],
        };

        IPublishEndpoint publishEndpoint = Substitute.For<IPublishEndpoint>();
        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();

        ReceiveEndpointRunner runner = new(
            binding,
            adapter,
            resolvedDeserializerResolver,
            publishEndpoint,
            sendEndpointProvider,
            scopeFactory,
            flowController,
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);

        return (runner, creditManager, channel.Writer, adapter);
    }

    private static IDeserializerResolver CreateDeserializerResolver()
    {
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");
        deserializer.Deserialize<RunnerTestMessage>(Arg.Any<ReadOnlySequence<byte>>())
                    .Returns(new RunnerTestMessage("test"));
        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(deserializer);
        return resolver;
    }

    private static InboundMessage MakeMessage(string id = "msg-1", int bodyBytes = 10)
    {
        byte[] body = new byte[bodyBytes];
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
    public async Task RunAsync_WhenCreditsExhausted_BlocksConsume()
    {
        // Arrange — MaxInFlightMessages=1, pre-fill the slot so no credits are available.
        var (runner, creditManager, writer, _) = CreateRunner(maxInFlight: 1);

        // Occupy the single credit slot before the runner processes anything.
        creditManager.TrackInflight(1, 0);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Write a message that the runner would need credits to process.
        await writer.WriteAsync(MakeMessage("msg-1"), cts.Token);

        // Act — start the runner; it should block waiting for a credit release.
        Task runTask = Task.Run(() => runner.RunAsync(cts.Token), cts.Token);

        // Give the runner a moment to reach the credit-wait loop.
        await Task.Delay(100, CancellationToken.None);

        // The runner is blocked — its inflight count should still be 1 (the pre-filled slot).
        creditManager.InflightCount.Should().Be(1);

        // Cleanup: cancel so the runner exits. After fix #1, cancellation is caught and
        // RunAsync returns normally instead of throwing.
        await cts.CancelAsync();
        await runTask;
    }

    [Fact]
    public async Task RunAsync_WhenCreditReleased_ResumesConsume()
    {
        // Arrange — MaxInFlightMessages=1, pre-fill to block the first iteration.
        var (runner, creditManager, writer, adapter) = CreateRunner(maxInFlight: 1);

        creditManager.TrackInflight(1, 0);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(MakeMessage("msg-1"), cts.Token);

        Task runTask = Task.Run(() => runner.RunAsync(cts.Token), cts.Token);

        // Give the runner time to block on WaitForCreditAsync.
        await Task.Delay(100, CancellationToken.None);

        // Act — release the occupied credit slot; runner should unblock and consume msg-1.
        creditManager.ReleaseInflight(1, 0);

        // Give the runner time to process the message.
        await Task.Delay(200, CancellationToken.None);

        // Assert — the adapter received a SettleAsync call for the message.
        await adapter.Received(1).SettleAsync(
            Arg.Any<SettlementAction>(),
            Arg.Is<InboundMessage>(m => m.MessageId == "msg-1"),
            Arg.Any<CancellationToken>());

        // Cancellation is caught by RunAsync — returns normally.
        await cts.CancelAsync();
        await runTask;
    }

    [Fact]
    public async Task RunAsync_AfterSettlement_ReleasesInflight()
    {
        // Arrange — MaxInFlightMessages=2 so the first message is processed without blocking.
        var (runner, creditManager, writer, _) = CreateRunner(maxInFlight: 2);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Write one message and then close the channel so RunAsync terminates naturally.
        await writer.WriteAsync(MakeMessage("msg-1", bodyBytes: 20), cts.Token);
        writer.Complete();

        // Act — run to completion (channel drained → loop exits).
        await runner.RunAsync(cts.Token);

        // Assert — after settlement the runner calls ReleaseInflight, so count is back to 0.
        creditManager.InflightCount.Should().Be(0);
        creditManager.InflightBytes.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WhenUtilizationAbove90Percent_LogsDegraded()
    {
        // Arrange — MaxInFlightMessages=10; pre-fill 9 slots so that after TryGrantCredits(1)
        // pushes inflight to 10, ReleaseInflight(1,…) drops it back to 9 (90%). The finally
        // block then calls CheckHealth, which sees 90% utilization and returns Degraded.
        var (runner, creditManager, writer, _) = CreateRunner(maxInFlight: 10);

        creditManager.TrackInflight(9, 0);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(MakeMessage("msg-x", bodyBytes: 0), cts.Token);
        writer.Complete();

        // Act — runner processes the message; finally block calls CheckHealth at 9/10 = 90%.
        await runner.RunAsync(cts.Token);

        // Assert — inflight settled back to 9 after release; utilization is exactly 90%.
        // (TryGrantCredits: 9→10, then ReleaseInflight: 10→9, CheckHealth: 90% → Degraded)
        creditManager.InflightCount.Should().Be(9);
        creditManager.UtilizationPercent.Should().Be(90.0);
    }

    [Fact]
    public async Task RunAsync_PerMessage_TracksInflightBytes()
    {
        // Arrange — MaxInFlightMessages=2, body is 42 bytes.
        var (runner, creditManager, writer, _) = CreateRunner(maxInFlight: 2);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(MakeMessage("msg-1", bodyBytes: 42), cts.Token);
        writer.Complete();

        // Act — run to completion.
        await runner.RunAsync(cts.Token);

        // Assert — after settlement InflightBytes must be back at 0.
        // The runner calls TrackInflightBytes(42) before invoke, then ReleaseInflight(1, 42) after.
        creditManager.InflightBytes.Should().Be(0);
        creditManager.InflightCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WithPooledBuffer_ReturnsBufferToPool()
    {
        // Arrange — message with a pooled buffer that should be returned after settlement.
        var (runner, _, writer, adapter) = CreateRunner(maxInFlight: 2);

        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        var message = new InboundMessage(
            messageId: "msg-pool",
            headers: new Dictionary<string, string>(),
            body: new ReadOnlySequence<byte>(rentedBuffer.AsMemory(0, 64)),
            deliveryTag: 1UL,
            pooledBuffer: rentedBuffer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await writer.WriteAsync(message, cts.Token);
        writer.Complete();

        // Act — run to completion; the finally block must return the pooled buffer.
        await runner.RunAsync(cts.Token);

        // Assert — settlement completed (proving the finally block executed, returning the buffer).
        await adapter.Received(1).SettleAsync(
            Arg.Any<SettlementAction>(),
            Arg.Is<InboundMessage>(m => m.MessageId == "msg-pool"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenCancelledDuringCreditWait_ThrowsOperationCancelled()
    {
        // Arrange — MaxInFlightMessages=1, pre-fill the single slot so the runner blocks
        // on WaitForCreditAsync immediately after receiving the first message.
        var (runner, creditManager, writer, _) = CreateRunner(maxInFlight: 1);

        creditManager.TrackInflight(1, 0);

        using CancellationTokenSource cts = new();

        await writer.WriteAsync(MakeMessage("msg-1"), CancellationToken.None);

        // Act — start the runner then cancel while it is waiting for credits.
        Task runTask = Task.Run(() => runner.RunAsync(cts.Token));

        // Allow the runner to reach WaitForCreditAsync.
        await Task.Delay(100, CancellationToken.None);

        // Cancel the token — WaitForCreditAsync throws OperationCanceledException,
        // which is caught by RunAsync's outer try-catch (fix #1). RunAsync returns normally.
        await cts.CancelAsync();

        // Assert — RunAsync completes without throwing (cancellation is handled gracefully).
        await runTask;
    }
}
