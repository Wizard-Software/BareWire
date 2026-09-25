using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Testing;

namespace BareWire.IntegrationTests;

/// <summary>
/// Integration tests for the publish-side pipeline and transport-level routing using
/// <see cref="BareWireTestHarness"/> and its underlying in-memory transport adapter directly.
/// No external broker is required.
/// </summary>
public sealed class InMemoryPublishConsumeTests : IAsyncDisposable
{
    private BareWireTestHarness? _harness;

    // ── Test records ──────────────────────────────────────────────────────────

    private sealed record TestOrder(string OrderId, decimal Amount);

    // ── Publish-side tests (via BareWireTestHarness) ──────────────────────────

    [Fact]
    public async Task PublishAsync_TypedMessage_ReachesTransport()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        _harness = await BareWireTestHarness.CreateAsync(cancellationToken: cts.Token);
        TestOrder order = new("ORD-001", 99.99m);

        // Subscribe BEFORE publishing to avoid missing the MessageSent event.
        Task<OutboundMessage> waitTask = _harness.WaitForPublishAsync<TestOrder>(TimeSpan.FromSeconds(5));

        // Act
        await _harness.Bus.PublishAsync(order, cts.Token);

        // Assert — WaitForPublishAsync matches by routing key = typeof(T).FullName
        OutboundMessage received = await waitTask;
        received.Should().NotBeNull();
        received.RoutingKey.Should().Be(typeof(TestOrder).FullName);
        received.ContentType.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PublishRawAsync_RawMessage_ReachesTransport()
    {
        // Arrange
        // BareWireBus.PublishRawAsync uses RoutingKey = string.Empty, so WaitForPublishAsync<T>
        // (which matches by typeof(T).FullName) cannot be used for raw messages.
        // This test exercises the transport adapter directly, observing the MessageSent event.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(cancellationToken: cts.Token);

        byte[] payloadBytes = Encoding.UTF8.GetBytes("{\"type\":\"raw\"}");
        OutboundMessage? capturedMessage = null;
        harness.Adapter.MessageSent += msg => capturedMessage = msg;

        OutboundMessage outbound = new(
            routingKey: string.Empty,
            headers: new Dictionary<string, string>(),
            body: payloadBytes,
            contentType: "application/json");

        // Act
        IReadOnlyList<SendResult> results = await harness.Adapter.SendBatchAsync([outbound], cts.Token);

        // Assert — message is confirmed and the event fired with the correct metadata
        results.Should().HaveCount(1);
        results[0].IsConfirmed.Should().BeTrue();
        capturedMessage.Should().NotBeNull();
        capturedMessage!.RoutingKey.Should().Be(string.Empty);
        capturedMessage.ContentType.Should().Be("application/json");
        capturedMessage.Body.Length.Should().Be(payloadBytes.Length);
    }

    // ── Transport-level tests (harness's in-memory transport adapter directly) ─

    [Fact]
    public async Task SendBatchAsync_Message_IsAvailableViaConsumeAsync()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            null, null, null, t => t.ConfigureTopology(topo => topo.DeclareQueue("orders")), cts.Token);
        ObservingTransportAdapter adapter = harness.Adapter;

        byte[] body = Encoding.UTF8.GetBytes("{\"orderId\":\"ORD-042\",\"amount\":12.50}");
        OutboundMessage outbound = new(
            routingKey: "orders",
            headers: new Dictionary<string, string> { ["x-source"] = "test" },
            body: body,
            contentType: "application/json");

        FlowControlOptions flowControl = new();

        // Act — send one message
        IReadOnlyList<SendResult> sendResults = await adapter.SendBatchAsync([outbound], cts.Token);

        // Assert — send confirmed
        sendResults.Should().HaveCount(1);
        sendResults[0].IsConfirmed.Should().BeTrue();
        sendResults[0].DeliveryTag.Should().BeGreaterThan(0UL);

        // Act — consume the message back from the same endpoint
        InboundMessage? received = null;
        await foreach (InboundMessage msg in adapter.ConsumeAsync("orders", flowControl, cts.Token))
        {
            received = msg;
            break; // take only the first message
        }

        // Assert — inbound message matches outbound body
        received.Should().NotBeNull();
        received!.MessageId.Should().NotBeNullOrEmpty();
        received.DeliveryTag.Should().Be(sendResults[0].DeliveryTag);
        received.Headers["x-source"].Should().Be("test");

        byte[] receivedBytes = ReadSequenceToArray(received.Body);
        receivedBytes.Should().BeEquivalentTo(body);
    }

    [Fact]
    public async Task SendBatchAsync_MultipleEndpoints_RoutesCorrectly()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            null, null, null,
            t => t.ConfigureTopology(topo =>
            {
                topo.DeclareQueue("orders");
                topo.DeclareQueue("payments");
            }),
            cts.Token);
        ObservingTransportAdapter adapter = harness.Adapter;

        byte[] ordersBody = Encoding.UTF8.GetBytes("{\"queue\":\"orders\"}");
        byte[] paymentsBody = Encoding.UTF8.GetBytes("{\"queue\":\"payments\"}");

        OutboundMessage ordersMessage = new(
            routingKey: "orders",
            headers: new Dictionary<string, string>(),
            body: ordersBody,
            contentType: "application/json");

        OutboundMessage paymentsMessage = new(
            routingKey: "payments",
            headers: new Dictionary<string, string>(),
            body: paymentsBody,
            contentType: "application/json");

        FlowControlOptions flowControl = new();

        // Act — send to two different routing keys in one batch
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(
            [ordersMessage, paymentsMessage],
            cts.Token);

        // Assert — both confirmed
        results.Should().HaveCount(2);
        results[0].IsConfirmed.Should().BeTrue();
        results[1].IsConfirmed.Should().BeTrue();

        // Act — consume from "orders" endpoint
        InboundMessage? ordersReceived = null;
        await foreach (InboundMessage msg in adapter.ConsumeAsync("orders", flowControl, cts.Token))
        {
            ordersReceived = msg;
            break;
        }

        // Act — consume from "payments" endpoint
        InboundMessage? paymentsReceived = null;
        await foreach (InboundMessage msg in adapter.ConsumeAsync("payments", flowControl, cts.Token))
        {
            paymentsReceived = msg;
            break;
        }

        // Assert — each endpoint received only its own message
        ordersReceived.Should().NotBeNull();
        ReadSequenceToArray(ordersReceived!.Body).Should().BeEquivalentTo(ordersBody);

        paymentsReceived.Should().NotBeNull();
        ReadSequenceToArray(paymentsReceived!.Body).Should().BeEquivalentTo(paymentsBody);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] ReadSequenceToArray(System.Buffers.ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
            return sequence.FirstSpan.ToArray();

        byte[] result = new byte[sequence.Length];
        int offset = 0;
        foreach (System.ReadOnlyMemory<byte> segment in sequence)
        {
            segment.Span.CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
            await _harness.DisposeAsync();
    }
}
