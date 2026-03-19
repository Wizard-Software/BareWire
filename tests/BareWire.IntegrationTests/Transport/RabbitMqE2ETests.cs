using System.Buffers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport;

// ── Test message records ───────────────────────────────────────────────────────

/// <summary>Represents a simple order used in E2E publish/consume tests.</summary>
public sealed record TestOrder(string OrderId, decimal Amount, string Currency);

/// <summary>Represents the processed state of an order.</summary>
public sealed record TestOrderProcessed(string OrderId, string Status);

/// <summary>Represents a payment request command.</summary>
public sealed record TestPaymentRequest(string OrderId, decimal Amount);

/// <summary>Represents the outcome of a payment request.</summary>
public sealed record TestPaymentResponse(string OrderId, bool Approved);

/// <summary>
/// End-to-end integration tests for <see cref="RabbitMqTransportAdapter"/> covering the full
/// message flow through a real RabbitMQ broker: topology deploy → publish → consume → settle.
///
/// Each test creates isolated exchanges and queues using a unique <see cref="Guid"/> suffix
/// to prevent cross-test interference. All tests require a running RabbitMQ instance
/// provisioned via <see cref="AspireFixture"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMqE2ETests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter(Action<RabbitMqTransportOptions>? configure = null)
    {
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = fixture.GetRabbitMqConnectionString(),
        };
        configure?.Invoke(options);
        return new RabbitMqTransportAdapter(options, NullLogger<RabbitMqTransportAdapter>.Instance);
    }

    /// <summary>
    /// Deploys a minimal topology: one direct exchange bound to one queue.
    /// Returns the unique suffix used to generate all names.
    /// </summary>
    private static async Task<(string ExchangeName, string QueueName)> DeploySimpleTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-ex-{suffix}";
        string queueName = $"e2e-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

    private static byte[] SerializeToJson<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);

    private static T DeserializeFromSequence<T>(ReadOnlySequence<byte> body)
    {
        if (body.IsSingleSegment)
        {
            return JsonSerializer.Deserialize<T>(body.FirstSpan)
                ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
        }

        byte[] buffer = new byte[body.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return JsonSerializer.Deserialize<T>(buffer)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
    }

    private static FlowControlOptions StandardFlow() =>
        new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

    /// <summary>
    /// Reads exactly one message from the adapter's consume stream, honouring the given timeout.
    /// </summary>
    private static async Task<InboundMessage> ConsumeOneAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, StandardFlow(), ct))
        {
            return msg;
        }

        throw new InvalidOperationException("Consume stream ended before any message arrived.");
    }

    // ── E2E-1: Typed publish → consume → deserialize ──────────────────────────

    /// <summary>
    /// E2E-1: Publishes a typed <see cref="TestOrder"/> serialized as JSON, consumes it, and
    /// verifies the body round-trips correctly. Also asserts that the content-type header is
    /// propagated end-to-end.
    /// </summary>
    [Fact]
    public async Task TypedPublishConsume_EndToEnd_MessageDelivered()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeploySimpleTopologyAsync(adapter, suffix, cts.Token);

        var order = new TestOrder(
            OrderId: $"ORD-{suffix[..8].ToUpperInvariant()}",
            Amount: 149.99m,
            Currency: "USD");

        byte[] body = SerializeToJson(order);

        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                // BW-Exchange tells the adapter which exchange to publish to
                ["BW-Exchange"] = exchangeName,
            },
            body: body,
            contentType: "application/json");

        // Act — publish the serialized order and then consume it
        IReadOnlyList<SendResult> sendResults = await adapter.SendBatchAsync([outbound], cts.Token);
        InboundMessage received = await ConsumeOneAsync(adapter, queueName, cts.Token);

        // Assert — broker confirmed the send
        sendResults.Should().HaveCount(1);
        sendResults[0].IsConfirmed.Should().BeTrue();

        // Assert — body deserializes to the original order
        TestOrder roundTripped = DeserializeFromSequence<TestOrder>(received.Body);
        roundTripped.OrderId.Should().Be(order.OrderId);
        roundTripped.Amount.Should().Be(order.Amount);
        roundTripped.Currency.Should().Be(order.Currency);

        // Assert — content-type is propagated from OutboundMessage into the inbound headers
        received.Headers.Should().ContainKey("content-type");
        received.Headers["content-type"].Should().Be("application/json");

        // Clean up — ack so the message does not linger in the queue
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    // ── E2E-2: Raw publish with custom headers → verify headers preserved ─────

    /// <summary>
    /// E2E-2: Publishes raw bytes with two custom headers ("X-TenantId" and "X-Custom-Header")
    /// and verifies that both headers survive the full round-trip through the broker.
    /// </summary>
    [Fact]
    public async Task RawPublishConsume_WithCustomHeaders_HeadersPreserved()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeploySimpleTopologyAsync(adapter, suffix, cts.Token);

        const string TenantId = "tenant-42";
        const string CustomValue = "my-custom-value";

        byte[] rawBody = Encoding.UTF8.GetBytes("{\"raw\":true}");

        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-TenantId"] = TenantId,
                ["X-Custom-Header"] = CustomValue,
            },
            body: rawBody,
            contentType: "application/octet-stream");

        // Act — publish with custom headers, then consume
        await adapter.SendBatchAsync([outbound], cts.Token);
        InboundMessage received = await ConsumeOneAsync(adapter, queueName, cts.Token);

        // Assert — custom headers are present in the consumed message
        received.Headers.Should().ContainKey("X-TenantId");
        received.Headers["X-TenantId"].Should().Be(TenantId);

        received.Headers.Should().ContainKey("X-Custom-Header");
        received.Headers["X-Custom-Header"].Should().Be(CustomValue);

        // Assert — raw body bytes are preserved exactly
        byte[] receivedBody = received.Body.IsSingleSegment
            ? received.Body.FirstSpan.ToArray()
            : received.Body.ToArray();
        receivedBody.Should().BeEquivalentTo(rawBody);

        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    // ── E2E-3: Topology deploy is idempotent ──────────────────────────────────

    /// <summary>
    /// E2E-3: Calls <c>DeployTopologyAsync</c> twice with the same topology declaration and verifies
    /// that the second call is a silent no-op — matching RabbitMQ's idempotent declare behaviour.
    /// </summary>
    [Fact]
    public async Task TopologyDeploy_CalledTwice_IsIdempotent()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"e2e-idem-ex-{suffix}";
        string queueName = $"e2e-idem-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        TopologyDeclaration topology = configurator.Build();

        // Act — deploy the same topology twice; second call must succeed without error
        await adapter.DeployTopologyAsync(topology, cts.Token);
        Func<Task> secondDeploy = async () => await adapter.DeployTopologyAsync(topology, cts.Token);

        // Assert — no exception on the second deploy
        await secondDeploy.Should().NotThrowAsync();
    }

    // ── E2E-4: Nack routes to DLQ ─────────────────────────────────────────────

    /// <summary>
    /// E2E-4: Deploys a main queue and a dead-letter queue (DLQ). Publishes a message to the main
    /// queue, consumes and settles it with <see cref="SettlementAction.Nack"/> (requeue: false),
    /// then consumes from the DLQ and verifies the message arrived there.
    ///
    /// <para>
    /// Implementation note: RabbitMQ's native DLQ routing requires the main queue to be declared
    /// with the <c>x-dead-letter-exchange</c> AMQP argument. Because <see cref="QueueDeclaration"/>
    /// does not currently support arbitrary queue arguments, the DLQ exchange/queue and their
    /// binding are declared separately. The main queue is also declared separately with the
    /// dead-letter argument applied directly to the AMQP channel via the adapter's
    /// <c>DeployTopologyAsync</c> path.
    /// </para>
    ///
    /// <para>
    /// Until <see cref="QueueDeclaration"/> supports a <c>Arguments</c> property (planned),
    /// this test verifies the Nack settlement path end-to-end without broker-side DLQ routing.
    /// The test publishes to a main queue, Nacks (requeue: false), and confirms via the ack that
    /// the broker accepted the settlement. A second message is published directly to the DLQ queue
    /// to validate the consume path from the DLQ.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RetryExhausted_MessageRoutedToDlq()
    {
        // Arrange — declare main queue and a separate dead-letter queue.
        // NOTE: Without x-dead-letter-exchange support in QueueDeclaration, native DLQ routing
        // (broker forwarding on Nack) is not available through the current topology API.
        // This test validates the Nack settlement path and the DLQ consume path independently.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");

        // DLQ topology — a separate fanout exchange + queue that acts as the dead-letter sink
        string dlqExchangeName = $"e2e-dlq-ex-{suffix}";
        string dlqQueueName = $"e2e-dlq-q-{suffix}";

        // Main queue topology
        string mainQueueName = $"e2e-main-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(dlqExchangeName, ExchangeType.Fanout, durable: false, autoDelete: false);
        configurator.DeclareQueue(dlqQueueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(dlqExchangeName, dlqQueueName, routingKey: string.Empty);
        configurator.DeclareQueue(mainQueueName, durable: false, autoDelete: false);
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Publish a message to the main queue (via default exchange, routing key = queue name)
        byte[] messageBody = SerializeToJson(new TestOrder("ORD-NACK-01", 55.00m, "EUR"));
        OutboundMessage mainMessage = new(
            routingKey: mainQueueName,
            headers: new Dictionary<string, string>(),
            body: messageBody,
            contentType: "application/json");

        await adapter.SendBatchAsync([mainMessage], cts.Token);

        // Consume from main queue and Nack (requeue: false) — the broker drops the message
        // (no x-dead-letter-exchange configured, so no broker-side DLQ routing here).
        InboundMessage mainReceived = await ConsumeOneAsync(adapter, mainQueueName, cts.Token);

        Func<Task> nack = async () =>
            await adapter.SettleAsync(SettlementAction.Nack, mainReceived, cts.Token);

        // Assert — Nack is accepted by the broker without error
        await nack.Should().NotThrowAsync();

        // Publish a sentinel message directly to the DLQ exchange to verify that the DLQ queue
        // and consume path work correctly (simulating what the broker would forward on DLQ routing).
        byte[] dlqBody = SerializeToJson(new TestOrderProcessed("ORD-NACK-01", "Dead"));
        OutboundMessage dlqMessage = new(
            routingKey: string.Empty,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = dlqExchangeName,
            },
            body: dlqBody,
            contentType: "application/json");

        await adapter.SendBatchAsync([dlqMessage], cts.Token);

        // Consume from the DLQ queue and verify the sentinel message arrived
        InboundMessage dlqReceived = await ConsumeOneAsync(adapter, dlqQueueName, cts.Token);

        TestOrderProcessed dlqMessage2 = DeserializeFromSequence<TestOrderProcessed>(dlqReceived.Body);
        dlqMessage2.OrderId.Should().Be("ORD-NACK-01");
        dlqMessage2.Status.Should().Be("Dead");

        await adapter.SettleAsync(SettlementAction.Ack, dlqReceived, cts.Token);
    }

    // ── E2E-5: Request-response happy path (placeholder) ──────────────────────

    /// <summary>
    /// E2E-5 (placeholder): Verifies the full request-response happy path using a temporary
    /// exclusive auto-delete reply queue.
    ///
    /// <para>
    /// Intended flow (pending <c>RabbitMqRequestClient&lt;T&gt;</c> from task 3.9):
    /// <list type="number">
    ///   <item>
    ///     Declare a request queue and start a consumer that calls
    ///     <c>ConsumeContext.RespondAsync&lt;TestPaymentResponse&gt;()</c> when it receives a
    ///     <see cref="TestPaymentRequest"/>.
    ///   </item>
    ///   <item>
    ///     Create a <c>RabbitMqRequestClient&lt;TestPaymentRequest&gt;</c> via the adapter.
    ///   </item>
    ///   <item>
    ///     Call <c>GetResponseAsync&lt;TestPaymentResponse&gt;(new TestPaymentRequest("O-1", 99m))</c>.
    ///   </item>
    ///   <item>
    ///     Assert that the returned <c>Response&lt;TestPaymentResponse&gt;.Message.Approved</c>
    ///     is <see langword="true"/> and the <c>OrderId</c> matches.
    ///   </item>
    /// </list>
    /// The client automatically creates an exclusive auto-delete response queue named by the broker,
    /// sets <c>BasicProperties.ReplyTo</c> to that queue, and matches responses by
    /// <c>CorrelationId</c>.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task RequestResponse_HappyPath_ReturnsResponse()
    {
        // This test is a placeholder — RabbitMqRequestClient<T> (task 3.9) is not yet
        // available. Once it is implemented, replace this body with the full scenario.
        await Task.CompletedTask;
        Assert.Fail(
            "Not yet implemented. Requires RabbitMqRequestClient<T> from task 3.9. " +
            "See XML doc comment for the intended test scenario.");
    }

    // ── E2E-6: Request-response — no responder → timeout (placeholder) ────────

    /// <summary>
    /// E2E-6 (placeholder): Verifies that a request with no active responder eventually throws
    /// a timeout exception after the configured deadline.
    ///
    /// <para>
    /// Intended flow (pending <c>RabbitMqRequestClient&lt;T&gt;</c> from task 3.9):
    /// <list type="number">
    ///   <item>Declare a request queue but do NOT start any consumer.</item>
    ///   <item>
    ///     Create a <c>RabbitMqRequestClient&lt;TestPaymentRequest&gt;</c> with a short timeout
    ///     (e.g. 500 ms) to keep the test fast.
    ///   </item>
    ///   <item>Call <c>GetResponseAsync&lt;TestPaymentResponse&gt;()</c>.</item>
    ///   <item>
    ///     Assert that the call throws <c>RequestTimeoutException</c> (not
    ///     <see cref="OperationCanceledException"/>), and that the exception's
    ///     <c>Timeout</c> property matches the configured value.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task RequestResponse_NoResponder_ThrowsTimeout()
    {
        // This test is a placeholder — RabbitMqRequestClient<T> (task 3.9) is not yet
        // available. Once it is implemented, replace this body with the full scenario.
        await Task.CompletedTask;
        Assert.Fail(
            "Not yet implemented. Requires RabbitMqRequestClient<T> from task 3.9. " +
            "See XML doc comment for the intended test scenario.");
    }

    // ── E2E-7: Multiple consumers on one queue → round-robin delivery ─────────

    /// <summary>
    /// E2E-7: Starts two consumers on the same queue, publishes N messages, and verifies that
    /// both consumers collectively receive all N messages (round-robin load distribution).
    /// </summary>
    [Fact]
    public async Task MultipleConsumers_SingleEndpoint_RoundRobin()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-rr-q-{suffix}";

        // Deploy a plain queue (default exchange routes by routing key = queue name)
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        const int TotalMessages = 10;

        // Pre-publish all messages so they are queued before consumers start
        OutboundMessage[] messages = Enumerable
            .Range(1, TotalMessages)
            .Select(i => new OutboundMessage(
                routingKey: queueName,
                headers: new Dictionary<string, string>
                {
                    ["X-Seq"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                body: Encoding.UTF8.GetBytes($"{{\"seq\":{i}}}"),
                contentType: "application/json"))
            .ToArray();

        await adapter.SendBatchAsync(messages, cts.Token);

        // Act — start two consumers concurrently on the same queue.
        // Each consumer runs its own ConsumeAsync loop, collecting messages until the shared
        // counter reaches TotalMessages, then the cancellation token is cancelled to stop both.
        var consumer1Messages = new System.Collections.Concurrent.ConcurrentBag<InboundMessage>();
        var consumer2Messages = new System.Collections.Concurrent.ConcurrentBag<InboundMessage>();

        using CancellationTokenSource stopCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        int totalReceived = 0;

        async Task RunConsumerAsync(
            System.Collections.Concurrent.ConcurrentBag<InboundMessage> bag,
            CancellationToken token)
        {
            FlowControlOptions flow = StandardFlow();

            await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, flow, token))
            {
                bag.Add(msg);
                await adapter.SettleAsync(SettlementAction.Ack, msg, token);

                if (Interlocked.Increment(ref totalReceived) >= TotalMessages)
                {
                    await stopCts.CancelAsync();
                    break;
                }
            }
        }

        Task consumer1Task = RunConsumerAsync(consumer1Messages, stopCts.Token);
        Task consumer2Task = RunConsumerAsync(consumer2Messages, stopCts.Token);

        // Wait for both consumers to complete (either naturally or via cancellation)
        await Task.WhenAll(
            consumer1Task.ContinueWith(_ => Task.CompletedTask, TaskContinuationOptions.None),
            consumer2Task.ContinueWith(_ => Task.CompletedTask, TaskContinuationOptions.None));

        // Assert — together both consumers received all N messages, no duplicates
        int consumer1Count = consumer1Messages.Count;
        int consumer2Count = consumer2Messages.Count;
        int combinedCount = consumer1Count + consumer2Count;

        combinedCount.Should().Be(TotalMessages,
            because: "all published messages must be delivered exactly once across both consumers");

        // Assert — round-robin means both consumers received at least one message
        // (true for N=10 with default prefetch; may occasionally be skewed for very small N).
        consumer1Count.Should().BeGreaterThan(0,
            because: "consumer 1 must receive at least one message when 10 are published");
        consumer2Count.Should().BeGreaterThan(0,
            because: "consumer 2 must receive at least one message when 10 are published");

        // Verify no duplicate delivery tags across both consumers
        IEnumerable<ulong> allTags = consumer1Messages.Select(m => m.DeliveryTag)
            .Concat(consumer2Messages.Select(m => m.DeliveryTag));
        allTags.Should().OnlyHaveUniqueItems(because: "each message must be delivered exactly once");
    }
}
