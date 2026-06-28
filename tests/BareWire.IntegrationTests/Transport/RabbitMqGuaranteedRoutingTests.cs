using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for the opt-in guaranteed-routing mode of
/// <see cref="RabbitMqTransportAdapter.SendBatchAsync"/>.
/// Each test uses a real RabbitMQ instance provisioned via <see cref="AspireFixture"/> and an
/// isolated topology with unique exchange/queue names to prevent cross-test interference.
/// </summary>
/// <remarks>
/// Routing matrix per test:
/// <list type="bullet">
///   <item><description>Unroutable = a declared exchange with no binding for the routing key used.</description></item>
///   <item><description>Routable = a declared exchange bound to a queue on the routing key used.</description></item>
/// </list>
/// With guaranteed routing ON the adapter publishes <c>mandatory: true</c>, so an unroutable
/// publication is returned by the broker (surfaced synchronously as <c>PublishException.IsReturn</c>)
/// and reported as <c>IsConfirmed: false</c>. With it OFF the behavior is bit-identical to the
/// historical default (<c>mandatory: false</c>, unroutable still reported as confirmed).
/// </remarks>
public sealed class RabbitMqGuaranteedRoutingTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Factory & helpers ──────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter(Action<RabbitMqTransportOptions>? configure = null)
    {
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = fixture.GetRabbitMqConnectionString(),
        };
        configure?.Invoke(options);
        return new RabbitMqTransportAdapter(options, NullLogger<RabbitMqTransportAdapter>.Instance);
    }

    private static OutboundMessage MakeMessage(string exchange, string routingKey, string payload = "{\"ok\":true}") =>
        new(
            routingKey: routingKey,
            headers: new Dictionary<string, string> { ["BW-Exchange"] = exchange },
            body: Encoding.UTF8.GetBytes(payload),
            contentType: "application/json");

    private static async Task<InboundMessage> ConsumeOneAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, flow, ct))
        {
            return msg;
        }

        throw new InvalidOperationException("Consume stream ended before a message was received.");
    }

    private static byte[] ReadSequenceToArray(ReadOnlySequence<byte> seq)
    {
        if (seq.IsSingleSegment)
        {
            return seq.FirstSpan.ToArray();
        }

        byte[] buf = new byte[seq.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> seg in seq)
        {
            seg.Span.CopyTo(buf.AsSpan(offset));
            offset += seg.Length;
        }

        return buf;
    }

    // ── ON + unroutable → IsConfirmed:false (the fix) ──────────────────────────

    /// <summary>
    /// With guaranteed routing enabled, publishing to a declared exchange on a routing key that has
    /// no binding is returned by the broker and reported as <c>IsConfirmed: false</c> — closing the
    /// silent-loss gap. This is the RED→GREEN test: before the adapter change it fails because the
    /// unroutable publish is reported as confirmed.
    /// </summary>
    [Fact]
    public async Task SendBatchAsync_WhenGuaranteedRoutingEnabled_AndRoutingKeyHasNoBinding_ReturnsIsConfirmedFalse()
    {
        // Arrange — exchange declared, NO bound queue for the routing key.
        string id = Guid.NewGuid().ToString("N");
        string exchange = $"test-gr-unroutable-ex-{id}";

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await using RabbitMqTransportAdapter adapter = CreateAdapter(o => o.GuaranteedRouting = true);

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchange, ExchangeType.Direct, durable: false, autoDelete: false);
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Act — publish to a routing key with no binding.
        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync([MakeMessage(exchange, "no-such-binding")], cts.Token);

        // Assert — the unroutable publication is reported as NOT confirmed.
        results.Should().HaveCount(1);
        results[0].IsConfirmed.Should().BeFalse("an unroutable mandatory publish must surface as IsConfirmed:false");
    }

    // ── ON + routable → IsConfirmed:true + delivered ───────────────────────────

    /// <summary>
    /// With guaranteed routing enabled, a routable publication is confirmed and the message arrives
    /// in the bound queue (the happy path is unaffected by mandatory mode).
    /// </summary>
    [Fact]
    public async Task SendBatchAsync_WhenGuaranteedRoutingEnabled_AndRoutable_ReturnsIsConfirmedTrue()
    {
        // Arrange — exchange → queue binding on a known routing key.
        string id = Guid.NewGuid().ToString("N");
        string exchange = $"test-gr-routable-ex-{id}";
        string queue = $"test-gr-routable-q-{id}";
        const string routingKey = "rk-bound";

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await using RabbitMqTransportAdapter adapter = CreateAdapter(o => o.GuaranteedRouting = true);

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchange, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchange, queue, routingKey);
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        byte[] payload = Encoding.UTF8.GetBytes("{\"routable\":true}");

        // Act
        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync([MakeMessage(exchange, routingKey, "{\"routable\":true}")], cts.Token);

        // Assert 1 — confirmed.
        results.Should().HaveCount(1);
        results[0].IsConfirmed.Should().BeTrue();

        // Assert 2 — the message actually arrived in the bound queue.
        InboundMessage delivered = await ConsumeOneAsync(adapter, queue, cts.Token);
        ReadSequenceToArray(delivered.Body).Should().BeEquivalentTo(payload);
        await adapter.SettleAsync(SettlementAction.Ack, delivered, cts.Token);
    }

    // ── OFF + unroutable → IsConfirmed:true (regression guard) ─────────────────

    /// <summary>
    /// REGRESSION GUARD: with guaranteed routing disabled (the default), an unroutable publication is
    /// still reported as <c>IsConfirmed: true</c> — the historical at-most-once routing behavior is
    /// preserved bit-for-bit. This protects the deliberate default-OFF decision from accidental reversal.
    /// </summary>
    [Fact]
    public async Task SendBatchAsync_WhenGuaranteedRoutingDisabled_AndUnroutable_ReturnsIsConfirmedTrue()
    {
        // Arrange — exchange declared, NO bound queue; adapter left at the default (OFF).
        string id = Guid.NewGuid().ToString("N");
        string exchange = $"test-gr-default-unroutable-ex-{id}";

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await using RabbitMqTransportAdapter adapter = CreateAdapter(); // GuaranteedRouting defaults to false

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchange, ExchangeType.Direct, durable: false, autoDelete: false);
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Act — unroutable publish with the default mandatory:false path.
        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync([MakeMessage(exchange, "no-such-binding")], cts.Token);

        // Assert — default behavior unchanged: silently accepted, reported confirmed.
        results.Should().HaveCount(1);
        results[0].IsConfirmed.Should().BeTrue(
            "with guaranteed routing OFF the default at-most-once routing behavior must be preserved");
    }

    // ── ON + mixed batch → per-message results, batch not aborted ──────────────

    /// <summary>
    /// With guaranteed routing enabled, a batch mixing routable and unroutable messages maps each
    /// outcome to the correct <c>results[i]</c> index and does not abort the batch — confirmed for the
    /// routable indices, not-confirmed for the unroutable one.
    /// </summary>
    [Fact]
    public async Task SendBatchAsync_WhenGuaranteedRoutingEnabled_MixedRoutability_MapsPerMessageResults()
    {
        // Arrange — one exchange, one bound routing key; unroutable messages use an unbound key.
        string id = Guid.NewGuid().ToString("N");
        string exchange = $"test-gr-mixed-ex-{id}";
        string queue = $"test-gr-mixed-q-{id}";
        const string boundKey = "rk-bound";
        const string unboundKey = "rk-unbound";

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await using RabbitMqTransportAdapter adapter = CreateAdapter(o => o.GuaranteedRouting = true);

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchange, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchange, queue, boundKey);
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Batch order: routable, unroutable, routable.
        var batch = new[]
        {
            MakeMessage(exchange, boundKey, "{\"i\":0}"),
            MakeMessage(exchange, unboundKey, "{\"i\":1}"),
            MakeMessage(exchange, boundKey, "{\"i\":2}"),
        };

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(batch, cts.Token);

        // Assert — per-index mapping; batch not aborted by the middle return.
        results.Should().HaveCount(3);
        results[0].IsConfirmed.Should().BeTrue("index 0 is routable");
        results[1].IsConfirmed.Should().BeFalse("index 1 is unroutable");
        results[2].IsConfirmed.Should().BeTrue("index 2 is routable");

        // Assert 2 — both routable messages actually arrived (the unroutable one did not). Drain them on
        // a single consumer (one consumer with prefetch holds both) so the queue is left clean.
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };
        int drained = 0;
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queue, flow, cts.Token))
        {
            await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
            if (++drained == 2)
            {
                break;
            }
        }

        drained.Should().Be(2, "exactly the two routable messages (indices 0 and 2) should be in the queue");
    }
}
