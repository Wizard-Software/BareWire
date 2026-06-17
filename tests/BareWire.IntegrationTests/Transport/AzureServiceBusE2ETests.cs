using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Topology;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// End-to-end integration tests for the Azure Service Bus transport adapter. Each test is
/// gated behind the <c>BAREWIRE_ASB_CONNECTION_STRING</c> environment variable — absent the
/// variable the test reports as "Skipped" (never silently green).
/// </summary>
/// <remarks>
/// <para>
/// Resource isolation: every test creates a uniquely-named queue with a <see cref="Guid"/>
/// suffix and deletes it in a <c>finally</c> block via
/// <see cref="AzureServiceBusTestEnvironment.TryDeleteQueueAsync"/> (zero entity leak on the
/// real namespace).
/// </para>
/// <para>
/// Settlement contract: <see cref="AzureServiceBusTransportAdapter.SettleAsync"/> must be
/// called <em>inside</em> the <c>await foreach</c> enumeration while the consumer is active
/// (PeekLock is held only for the lifetime of the lock). All tests follow this contract via
/// the <c>ConsumeOneAndSettleAsync</c> helper.
/// </para>
/// </remarks>
[Trait("Category", "AzureServiceBus")]
public sealed class AzureServiceBusE2ETests
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    private static FlowControlOptions StandardFlow() =>
        new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

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

    private static byte[] ReadSequenceToArray(ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
        {
            return sequence.FirstSpan.ToArray();
        }

        byte[] result = new byte[sequence.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in sequence)
        {
            segment.Span.CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    /// <summary>
    /// Consumes exactly one message, runs an optional <paramref name="inspect"/> callback,
    /// then settles it with <paramref name="action"/> — all before the enumerator is disposed
    /// (PeekLock constraint). Returns the consumed message for further assertions.
    /// </summary>
    private static async Task<InboundMessage> ConsumeOneAndSettleAsync(
        AzureServiceBusTransportAdapter adapter,
        string queueName,
        SettlementAction action,
        CancellationToken ct,
        Action<InboundMessage>? inspect = null)
    {
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, StandardFlow(), ct))
        {
            inspect?.Invoke(msg);
            await adapter.SettleAsync(action, msg, ct);
            return msg;
        }

        throw new InvalidOperationException("Consume stream ended before any message arrived.");
    }

    // ── Message record used across tests ──────────────────────────────────────

    /// <summary>Typed order record used as a round-trip payload in E2E tests.</summary>
    public sealed record TestAsbOrder(string OrderId, decimal Amount, string Currency);

    // ── E2E-ASB-1: Typed publish → consume → deserialize ─────────────────────

    /// <summary>
    /// E2E-ASB-1: Publishes a typed <see cref="TestAsbOrder"/> serialised as JSON, consumes it,
    /// deserialises the body, and asserts field equality. Also verifies that the
    /// <c>BW-Queue</c> delivery header is stamped by the consumer (the Azure Service Bus consumer
    /// stamps <c>BW-ConsumerId</c> and <c>BW-Queue</c>; it does not emit a <c>BW-Topic</c> header).
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task TypedPublishConsume_EndToEnd_MessageDelivered()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-asb-typed-{suffix}";

        await using AzureServiceBusTransportAdapter adapter =
            AzureServiceBusTestEnvironment.CreateSasAdapter();

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(Name: queueName, Durable: true),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            var order = new TestAsbOrder(
                OrderId: $"ORD-{suffix[..8].ToUpperInvariant()}",
                Amount: 149.99m,
                Currency: "USD");

            byte[] body = SerializeToJson(order);

            OutboundMessage outbound = new(
                routingKey: queueName,
                headers: new Dictionary<string, string>(),
                body: body,
                contentType: "application/json");

            // Act — publish
            IReadOnlyList<SendResult> sendResults =
                await adapter.SendBatchAsync([outbound], cts.Token);

            // Assert — broker confirmed send
            sendResults.Should().HaveCount(1);
            sendResults[0].IsConfirmed.Should().BeTrue();

            // Act + Assert — consume, deserialise, verify headers
            TestAsbOrder? roundTripped = null;
            await ConsumeOneAndSettleAsync(
                adapter,
                queueName,
                SettlementAction.Ack,
                cts.Token,
                inspect: msg =>
                {
                    roundTripped = DeserializeFromSequence<TestAsbOrder>(msg.Body);
                    msg.Headers.Should().ContainKey("BW-Queue");
                });

            roundTripped.Should().NotBeNull();
            roundTripped!.OrderId.Should().Be(order.OrderId);
            roundTripped.Amount.Should().Be(order.Amount);
            roundTripped.Currency.Should().Be(order.Currency);
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(
                queueName, CancellationToken.None);
        }
    }

    // ── E2E-ASB-2: Session FIFO ordering ─────────────────────────────────────

    /// <summary>
    /// E2E-ASB-2: Publishes N=8 messages with the same <c>BW-SessionId</c> to a
    /// session-enabled queue and asserts that they arrive in strictly ascending order
    /// (FIFO per SessionId guarantee).
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Sessions_SameSessionId_PreservesFifoOrder()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        // 60 s: covers session accept + publish + 8-message consume.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        const int TotalMessages = 8;
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-asb-session-{suffix}";

        // Session consume requires UseSessions() on the adapter.
        await using AzureServiceBusTransportAdapter adapter =
            AzureServiceBusTestEnvironment.CreateSasAdapter(c => c.UseSessions());

        try
        {
            // Queue must be created with RequiresSession=true — cannot be changed after creation.
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(
                        Name: queueName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            [AzureServiceBusTopologyArguments.RequiresSession] = true,
                        }),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            // Publish N messages with sequence number embedded in body and a shared session id.
            OutboundMessage[] messages = Enumerable
                .Range(1, TotalMessages)
                .Select(i => new OutboundMessage(
                    routingKey: queueName,
                    headers: new Dictionary<string, string>
                    {
                        ["BW-SessionId"] = "session-42",
                    },
                    body: System.Text.Encoding.UTF8.GetBytes($"{{\"seq\":{i}}}"),
                    contentType: "application/json"))
                .ToArray();

            await adapter.SendBatchAsync(messages, cts.Token);

            // Consume all N messages and collect the sequence numbers.
            var receivedSeqs = new List<int>(TotalMessages);

            await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, StandardFlow(), cts.Token))
            {
                byte[] bodyBytes = ReadSequenceToArray(msg.Body);
                using JsonDocument doc = JsonDocument.Parse(bodyBytes);
                int seq = doc.RootElement.GetProperty("seq").GetInt32();
                receivedSeqs.Add(seq);

                await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);

                if (receivedSeqs.Count == TotalMessages)
                {
                    break;
                }
            }

            // Assert — all messages received and in strictly ascending order.
            receivedSeqs.Should().HaveCount(TotalMessages);
            receivedSeqs.Should().BeInAscendingOrder(
                because: "messages with the same BW-SessionId must arrive in publish order (FIFO per session)");
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(
                queueName, CancellationToken.None);
        }
    }

    // ── E2E-ASB-3: Scheduled message delivered after delay ───────────────────

    /// <summary>
    /// E2E-ASB-3: Schedules a message with a ~10-second enqueue delay, verifies it does
    /// NOT arrive in the first ~2 seconds (pre-guard), then asserts it IS delivered within
    /// ~15 seconds after the scheduled time (post-window).
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task ScheduledMessage_DeliveredAfterDelay()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-asb-sched-{suffix}";

        await using AzureServiceBusTransportAdapter adapter =
            AzureServiceBusTestEnvironment.CreateSasAdapter();

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues = [new QueueDeclaration(Name: queueName, Durable: true)],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            // G1-B pinned timing: schedule 10 s into the future.
            DateTimeOffset scheduledEnqueueTime = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

            OutboundMessage outbound = new(
                routingKey: queueName,
                headers: new Dictionary<string, string>(),
                body: SerializeToJson(new { marker = "scheduled" }),
                contentType: "application/json");

            await adapter.ScheduleAsync(outbound, scheduledEnqueueTime, cts.Token);

            // Pre-guard: no message should arrive within the first ~2 seconds.
            using CancellationTokenSource preGuardCts =
                CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            preGuardCts.CancelAfter(TimeSpan.FromSeconds(2));

            bool arrivedEarly = false;

            try
            {
                await foreach (InboundMessage msg in adapter.ConsumeAsync(
                    queueName, StandardFlow(), preGuardCts.Token))
                {
                    arrivedEarly = true;
                    await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the pre-guard window expired without a message arriving.
            }

            arrivedEarly.Should().BeFalse(
                because: "the scheduled message must not be available before its enqueue time");

            // Post-window: message must arrive within ~15 s after the enqueue time.
            // The outer CTS (60 s) is the hard budget.
            bool arrivedOnTime = false;

            await foreach (InboundMessage msg in adapter.ConsumeAsync(
                queueName, StandardFlow(), cts.Token))
            {
                arrivedOnTime = true;
                await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
                break;
            }

            arrivedOnTime.Should().BeTrue(
                because: "the scheduled message must be delivered within the post-window");
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(
                queueName, CancellationToken.None);
        }
    }

    // ── E2E-ASB-4: Cancel scheduled message ──────────────────────────────────

    /// <summary>
    /// E2E-ASB-4: Schedules a message then immediately cancels it. Asserts the message
    /// does NOT arrive within a ~15-second observation window.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task CancelScheduled_BeforeEnqueue_NotDelivered()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-asb-cancel-{suffix}";

        await using AzureServiceBusTransportAdapter adapter =
            AzureServiceBusTestEnvironment.CreateSasAdapter();

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues = [new QueueDeclaration(Name: queueName, Durable: true)],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            DateTimeOffset scheduledEnqueueTime = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

            OutboundMessage outbound = new(
                routingKey: queueName,
                headers: new Dictionary<string, string>(),
                body: SerializeToJson(new { marker = "should-be-cancelled" }),
                contentType: "application/json");

            // Schedule then immediately cancel.
            ScheduledMessageToken token =
                await adapter.ScheduleAsync(outbound, scheduledEnqueueTime, cts.Token);
            await adapter.CancelScheduledAsync(token, cts.Token);

            // Observe for ~15 s — message must not arrive.
            using CancellationTokenSource observationCts =
                CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            observationCts.CancelAfter(TimeSpan.FromSeconds(15));

            bool arrived = false;

            try
            {
                await foreach (InboundMessage msg in adapter.ConsumeAsync(
                    queueName, StandardFlow(), observationCts.Token))
                {
                    arrived = true;
                    await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: observation window expired without a message.
            }

            arrived.Should().BeFalse(
                because: "a cancelled scheduled message must never be delivered");
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(
                queueName, CancellationToken.None);
        }
    }

    // ── E2E-ASB-5: Reject → DLQ sub-queue ────────────────────────────────────

    /// <summary>
    /// E2E-ASB-5: Settles a message with <see cref="SettlementAction.Reject"/> (maps to
    /// broker <c>DeadLetterMessageAsync</c>), then consumes from the
    /// <c>{queue}/$DeadLetterQueue</c> sub-queue and asserts the message is present.
    /// Acks the DLQ message to clean up.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task DeadLetter_OnReject_MovesToDlqSubQueue()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-asb-dlq-{suffix}";
        string dlqPath = $"{queueName}/$DeadLetterQueue";

        await using AzureServiceBusTransportAdapter adapter =
            AzureServiceBusTestEnvironment.CreateSasAdapter();

        try
        {
            // MaxDeliveryCount=1 so the message moves to DLQ after a single Reject
            // without needing multiple consume cycles.
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(
                        Name: queueName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            [AzureServiceBusTopologyArguments.MaxDeliveryCount] = 1,
                        }),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            OutboundMessage outbound = new(
                routingKey: queueName,
                headers: new Dictionary<string, string>(),
                body: SerializeToJson(new { marker = "to-dlq" }),
                contentType: "application/json");

            await adapter.SendBatchAsync([outbound], cts.Token);

            // Consume and reject → broker DeadLetterMessageAsync.
            await ConsumeOneAndSettleAsync(adapter, queueName, SettlementAction.Reject, cts.Token);

            // Assert — message is present in the DLQ sub-queue; ack it to clean up.
            InboundMessage dlqMessage =
                await ConsumeOneAndSettleAsync(adapter, dlqPath, SettlementAction.Ack, cts.Token);

            dlqMessage.Should().NotBeNull(
                because: "a Reject-settled message must move to the dead-letter sub-queue");
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(
                queueName, CancellationToken.None);
        }
    }

    // ── E2E-ASB-6: Nack → redelivery ─────────────────────────────────────────

    /// <summary>
    /// E2E-ASB-6: Settles a message with <see cref="SettlementAction.Nack"/> (maps to
    /// broker <c>AbandonMessageAsync</c>), then asserts the same message body is redelivered
    /// by the broker. A short <c>LockDuration</c> (5 s) and explicit <c>MaxDeliveryCount=10</c>
    /// ensure the test completes within the budget without depending on lock expiry.
    /// </summary>
    /// <remarks>
    /// The ASB transport consumer does not stamp a <c>BW-DeliveryCount</c> header, so
    /// redelivery is verified by receiving the same body content a second time.
    /// </remarks>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Settlement_Nack_RedeliversMessage()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-asb-nack-{suffix}";

        await using AzureServiceBusTransportAdapter adapter =
            AzureServiceBusTestEnvironment.CreateSasAdapter();

        try
        {
            // Short LockDuration: if the consumer hangs the lock expires quickly.
            // MaxDeliveryCount=10 so the redelivered message is not auto-DLQ'd.
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(
                        Name: queueName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            [AzureServiceBusTopologyArguments.LockDuration] = TimeSpan.FromSeconds(5),
                            [AzureServiceBusTopologyArguments.MaxDeliveryCount] = 10,
                        }),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            const string MarkerValue = "nack-me";

            OutboundMessage outbound = new(
                routingKey: queueName,
                headers: new Dictionary<string, string>(),
                body: SerializeToJson(new { marker = MarkerValue }),
                contentType: "application/json");

            await adapter.SendBatchAsync([outbound], cts.Token);

            // First consume — Nack (→ broker Abandon → immediate redelivery).
            string? firstBody = null;
            await ConsumeOneAndSettleAsync(
                adapter,
                queueName,
                SettlementAction.Nack,
                cts.Token,
                inspect: msg =>
                {
                    firstBody = System.Text.Encoding.UTF8.GetString(ReadSequenceToArray(msg.Body));
                });

            firstBody.Should().NotBeNullOrWhiteSpace();

            // Second consume — the same body must be redelivered (G1-B: redelivery verified
            // by body equality because the transport does not expose a delivery-count header).
            string? secondBody = null;
            await ConsumeOneAndSettleAsync(
                adapter,
                queueName,
                SettlementAction.Ack,
                cts.Token,
                inspect: msg =>
                {
                    secondBody = System.Text.Encoding.UTF8.GetString(ReadSequenceToArray(msg.Body));
                });

            secondBody.Should().NotBeNullOrWhiteSpace();
            secondBody.Should().Be(
                firstBody,
                because: "Nack (broker Abandon) must cause the same message to be redelivered");
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(
                queueName, CancellationToken.None);
        }
    }
}
