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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Unit tests for <see cref="BareWireBus.IsPublishIdle"/> and <see cref="BareWireBus.PublishBatchEpoch"/> —
/// the publish-loop quiescence signals consumed by <see cref="BareWireBusControl"/>'s graceful-drain
/// coordination during <see cref="BareWireBusControl.StopAsync"/>.
/// </summary>
public sealed class BareWireBus_PublishIdleTests
{
    private sealed record IdleProbe(int Value);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (BareWireBus Bus, TaskCompletionSource SendEntered, TaskCompletionSource ReleaseSend) CreateBus()
    {
        TaskCompletionSource sendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.SendBatchAsync(
                Arg.Any<IReadOnlyList<OutboundMessage>>(),
                Arg.Any<CancellationToken>())
               .Returns(async callInfo =>
               {
                   var messages = callInfo.ArgAt<IReadOnlyList<OutboundMessage>>(0);
                   sendEntered.TrySetResult();
                   await releaseSend.Task.ConfigureAwait(false);
                   return (IReadOnlyList<SendResult>)messages.Select(static _ => new SendResult(true, 0UL)).ToList();
               });

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer.When(s => s.Serialize(Arg.Any<IdleProbe>(), Arg.Any<IBufferWriter<byte>>()))
                  .Do(_ => { });

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();
        MiddlewareChain chain = new([]);
        MessagePipeline pipeline = new(chain, deserializerResolver, NullLogger<MessagePipeline>.Instance, new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        BareWireBus bus = new(
            adapter,
            new DefaultSerializerResolver(serializer),
            pipeline,
            flowController,
            new PublishFlowControlOptions(),
            NullLogger<BareWireBus>.Instance,
            new NullInstrumentation());

        return (bus, sendEntered, releaseSend);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        long deadline = DateTime.UtcNow.Ticks + timeout.Ticks;
        while (!predicate())
        {
            if (DateTime.UtcNow.Ticks > deadline)
                throw new TimeoutException("Predicate did not become true within the timeout.");

            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    // ── IsPublishIdle / PublishBatchEpoch ────────────────────────────────────

    [Fact]
    public async Task IsPublishIdle_WhileBatchSendInProgress_ReturnsFalse_ThenTrueAfterSend()
    {
        var (bus, sendEntered, releaseSend) = CreateBus();
        bus.StartPublishing();

        bus.IsPublishIdle.Should().BeTrue();
        long epochBefore = bus.PublishBatchEpoch;

        await bus.PublishAsync(new IdleProbe(1));
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The channel is already empty (the message was read into the batch) and the batch is
        // still in flight (SendBatchAsync has not returned yet) — IsPublishIdle must be false.
        bus.IsPublishIdle.Should().BeFalse();
        bus.PublishBatchEpoch.Should().Be(epochBefore + 1);

        releaseSend.SetResult();

        await WaitUntilAsync(() => bus.IsPublishIdle, TimeSpan.FromSeconds(5));
        bus.IsPublishIdle.Should().BeTrue();

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task IsPublishIdle_WhenMessageQueuedBeforePublishingStarted_ReturnsFalse()
    {
        var (bus, _, releaseSend) = CreateBus();

        // Publisher loop NOT started — the message stays queued in the channel.
        await bus.PublishAsync(new IdleProbe(1));

        bus.IsPublishIdle.Should().BeFalse();

        releaseSend.SetResult();
        await bus.DisposeAsync();
    }
}
