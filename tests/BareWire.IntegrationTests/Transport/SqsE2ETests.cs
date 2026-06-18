// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using System.Buffers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Topology;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// End-to-end integration tests for the Amazon SQS transport adapter. Each test is gated
/// behind the <c>BAREWIRE_SQS_SERVICE_URL</c> environment variable — absent the variable the
/// test reports as "Skipped" (never silently green).
/// </summary>
/// <remarks>
/// <para>
/// Resource isolation: every test creates uniquely-named queues with a <see cref="Guid"/>
/// suffix and deletes them in a <c>finally</c> block via
/// <see cref="SqsTestEnvironment.TryDeleteQueueAsync"/> (zero entity leak against the broker).
/// </para>
/// <para>
/// Settlement contract: <see cref="SqsTransportAdapter.SettleAsync"/> must be called
/// <em>inside</em> the <c>await foreach</c> enumeration while the consumer is active
/// (the receipt handle is only valid while held). All tests follow this contract.
/// </para>
/// </remarks>
[Trait("Category", "AwsSqs")]
public sealed class SqsE2ETests
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
    /// then settles it with <paramref name="action"/> — all before the enumerator advances.
    /// Returns the consumed message for further assertions.
    /// </summary>
    private static async Task<InboundMessage> ConsumeOneAndSettleAsync(
        SqsTransportAdapter adapter,
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
    public sealed record TestSqsOrder(string OrderId, decimal Amount, string Currency);

    // ── E2E-SQS-1: Standard queue roundtrip ──────────────────────────────────

    /// <summary>
    /// E2E-SQS-1: Publishes a typed <see cref="TestSqsOrder"/> serialised as JSON to a
    /// standard SQS queue, consumes it, deserialises the body, and asserts field equality.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task TypedPublishConsume_EndToEnd_MessageDelivered()
    {
        SqsTestEnvironment.SkipIfUnavailable();

        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-sqs-roundtrip-{suffix}";

        await using SqsTransportAdapter adapter = SqsTestEnvironment.CreateAdapter();

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues = [new QueueDeclaration(Name: queueName, Durable: true)],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            var order = new TestSqsOrder(
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
            sendResults[0].IsConfirmed.Should().BeTrue(
                because: "the SQS broker must confirm the message was accepted");

            // Act + Assert — consume, deserialise, verify round-trip
            TestSqsOrder? roundTripped = null;
            await ConsumeOneAndSettleAsync(
                adapter,
                queueName,
                SettlementAction.Ack,
                cts.Token,
                inspect: msg =>
                {
                    roundTripped = DeserializeFromSequence<TestSqsOrder>(msg.Body);
                });

            roundTripped.Should().NotBeNull();
            roundTripped!.OrderId.Should().Be(order.OrderId);
            roundTripped.Amount.Should().Be(order.Amount);
            roundTripped.Currency.Should().Be(order.Currency);
        }
        finally
        {
            await SqsTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }

    // ── E2E-SQS-2: FIFO ordering per MessageGroupId ──────────────────────────

    /// <summary>
    /// E2E-SQS-2: Publishes N=8 messages with the same <c>BW-MessageGroupId</c> to a FIFO
    /// queue and asserts that they arrive in strictly ascending order (FIFO per group guarantee).
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Fifo_SameMessageGroupId_PreservesOrder()
    {
        SqsTestEnvironment.SkipIfUnavailable();

        // 60 s: covers queue creation + batch publish + 8-message consume.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        const int TotalMessages = 8;
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-sqs-fifo-order-{suffix}.fifo";

        await using SqsTransportAdapter adapter = SqsTestEnvironment.CreateAdapter();

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(
                        Name: queueName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            [SqsTopologyArguments.FifoKey] = true,
                        }),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            // Publish N messages: each carries a seq number in the body and the same group id.
            OutboundMessage[] messages = Enumerable
                .Range(1, TotalMessages)
                .Select(i => new OutboundMessage(
                    routingKey: queueName,
                    headers: new Dictionary<string, string>
                    {
                        [SqsHeaderMapper.MessageGroupIdHeader] = "group-1",
                    },
                    body: Encoding.UTF8.GetBytes($"{{\"seq\":{i}}}"),
                    contentType: "application/json"))
                .ToArray();

            await adapter.SendBatchAsync(messages, cts.Token);

            // Consume all N messages and collect seq numbers.
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

            // Assert — all received and in strictly ascending order.
            receivedSeqs.Should().HaveCount(TotalMessages,
                because: "all 8 published messages must be delivered to the consumer");
            receivedSeqs.Should().BeInAscendingOrder(
                because: "messages with the same BW-MessageGroupId must arrive in publish order (FIFO per group)");
        }
        finally
        {
            await SqsTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }

    // ── E2E-SQS-3: Content-based deduplication ────────────────────────────────

    /// <summary>
    /// E2E-SQS-3: Publishes the same body twice to a FIFO queue with content-based
    /// deduplication enabled and asserts that the consumer receives exactly one message
    /// (the duplicate is dropped by the broker within the 5-minute dedup window).
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Fifo_ContentBasedDeduplication_DropsDuplicateInWindow()
    {
        SqsTestEnvironment.SkipIfUnavailable();

        // 60 s: covers queue creation + two publishes + drain loop.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-sqs-dedup-{suffix}.fifo";

        await using SqsTransportAdapter adapter =
            SqsTestEnvironment.CreateAdapter(c => c.ContentBasedDeduplication());

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(
                        Name: queueName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            [SqsTopologyArguments.FifoKey] = true,
                            [SqsTopologyArguments.ContentBasedDeduplicationKey] = true,
                        }),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            byte[] body = SerializeToJson(new { payload = "dedup-test", run = suffix[..8] });

            OutboundMessage MakeMessage() => new(
                routingKey: queueName,
                headers: new Dictionary<string, string>
                {
                    [SqsHeaderMapper.MessageGroupIdHeader] = "group-dedup",
                },
                body: body,
                contentType: "application/json");

            // Publish the SAME body twice — the broker should drop the second one.
            await adapter.SendBatchAsync([MakeMessage()], cts.Token);
            await adapter.SendBatchAsync([MakeMessage()], cts.Token);

            // Drain: collect messages until the budget expires or we see 2+ (failure).
            var receivedBodies = new List<byte[]>();

            // Use a short-lived linked CTS for the drain window so we don't block the full 60 s.
            using CancellationTokenSource drainCts =
                CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            drainCts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                await foreach (InboundMessage msg in
                    adapter.ConsumeAsync(queueName, StandardFlow(), drainCts.Token))
                {
                    receivedBodies.Add(ReadSequenceToArray(msg.Body));
                    await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);

                    if (receivedBodies.Count >= 2)
                    {
                        break; // Stop early — already saw more than expected.
                    }
                }
            }
            catch (OperationCanceledException) when (drainCts.IsCancellationRequested && !cts.IsCancellationRequested)
            {
                // Drain window expired — expected when only 1 message was delivered.
            }

            // Assert — exactly 1 message (duplicate dropped within the dedup window).
            receivedBodies.Should().HaveCount(1,
                because: "content-based deduplication must suppress the second identical publish within the 5-minute window");
        }
        finally
        {
            await SqsTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }

    // ── E2E-SQS-4: RedrivePolicy — Reject → DLQ ──────────────────────────────

    /// <summary>
    /// E2E-SQS-4: Verifies the RedrivePolicy end-to-end. Declares a DLQ <em>before</em> the
    /// source queue (GAP-1 ordering contract), configures <c>maxReceiveCount=1</c> and a short
    /// visibility timeout (PERF-2), publishes 1 message, Rejects it, then asserts the message
    /// is moved to the DLQ by the broker.
    /// </summary>
    /// <remarks>
    /// The DLQ queue MUST be declared first in <see cref="TopologyDeclaration.Queues"/>. The
    /// adapter resolves the DLQ ARN in a single-pass deploy from the queue-URL cache populated
    /// by the DLQ entry. Declaring source before DLQ causes a queue-not-found error.
    /// </remarks>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task DeadLetter_OnReject_AfterMaxReceiveCount_MovesToDlq()
    {
        SqsTestEnvironment.SkipIfUnavailable();

        // 60 s: covers two-queue deploy + publish + reject + DLQ poll.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string dlqName = $"e2e-sqs-dlq-{suffix}";
        string sourceName = $"e2e-sqs-src-{suffix}";

        await using SqsTransportAdapter adapter = SqsTestEnvironment.CreateAdapter();

        try
        {
            // GAP-1: DLQ MUST be declared BEFORE the source queue in a single-pass deploy.
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(Name: dlqName, Durable: true),
                    new QueueDeclaration(
                        Name: sourceName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            // PERF-2: short visibility timeout so the broker re-delivers
                            // quickly and increments receive count past maxReceiveCount=1.
                            [SqsTopologyArguments.VisibilityTimeout] = TimeSpan.FromSeconds(1),
                            [SqsTopologyArguments.MaxReceiveCountKey] = 1,
                            [SqsTopologyArguments.DeadLetterQueueName] = dlqName,
                        }),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            byte[] body = SerializeToJson(new { marker = "to-dlq" });

            OutboundMessage outbound = new(
                routingKey: sourceName,
                headers: new Dictionary<string, string>(),
                body: body,
                contentType: "application/json");

            await adapter.SendBatchAsync([outbound], cts.Token);

            // Consume from source and Reject — receipt handle is released without deletion.
            // The broker will re-deliver the message; because maxReceiveCount=1, after the
            // next receive the RedrivePolicy moves it to the DLQ automatically.
            await foreach (InboundMessage msg in adapter.ConsumeAsync(sourceName, StandardFlow(), cts.Token))
            {
                await adapter.SettleAsync(SettlementAction.Reject, msg, cts.Token);
                break;
            }

            // Poll the DLQ until the message arrives or the budget expires.
            bool arrivedInDlq = false;

            await foreach (InboundMessage dlqMsg in adapter.ConsumeAsync(dlqName, StandardFlow(), cts.Token))
            {
                arrivedInDlq = true;
                await adapter.SettleAsync(SettlementAction.Ack, dlqMsg, cts.Token);
                break;
            }

            arrivedInDlq.Should().BeTrue(
                because: "after Reject with maxReceiveCount=1, the RedrivePolicy must move the message to the DLQ");
        }
        finally
        {
            await SqsTestEnvironment.TryDeleteQueueAsync(sourceName, CancellationToken.None);
            await SqsTestEnvironment.TryDeleteQueueAsync(dlqName, CancellationToken.None);
        }
    }

    // ── E2E-SQS-5: Nack → redelivery ─────────────────────────────────────────

    /// <summary>
    /// E2E-SQS-5: Settles a message with <see cref="SettlementAction.Nack"/>
    /// (maps to <c>ChangeMessageVisibility(0)</c> — immediate redeliver), then consumes
    /// the redelivered message and asserts the body is identical. Acks on the second receive.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Settlement_Nack_RedeliversMessage()
    {
        SqsTestEnvironment.SkipIfUnavailable();

        // 60 s: covers queue creation + publish + two consume cycles.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-sqs-nack-{suffix}";

        await using SqsTransportAdapter adapter = SqsTestEnvironment.CreateAdapter();

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(
                        Name: queueName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            // Short visibility so the message becomes re-visible quickly
                            // after ChangeVisibility(0) if there is any broker latency.
                            [SqsTopologyArguments.VisibilityTimeout] = TimeSpan.FromSeconds(1),
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

            // First consume — Nack → ChangeVisibility(0) → immediate redeliver.
            string? firstBody = null;
            await ConsumeOneAndSettleAsync(
                adapter,
                queueName,
                SettlementAction.Nack,
                cts.Token,
                inspect: msg =>
                {
                    firstBody = Encoding.UTF8.GetString(ReadSequenceToArray(msg.Body));
                });

            firstBody.Should().NotBeNullOrWhiteSpace(
                because: "the first consume must deliver the published body");

            // Second consume — same body must be redelivered.
            string? secondBody = null;
            await ConsumeOneAndSettleAsync(
                adapter,
                queueName,
                SettlementAction.Ack,
                cts.Token,
                inspect: msg =>
                {
                    secondBody = Encoding.UTF8.GetString(ReadSequenceToArray(msg.Body));
                });

            secondBody.Should().NotBeNullOrWhiteSpace();
            secondBody.Should().Be(
                firstBody,
                because: "Nack (ChangeVisibility 0) must cause the same message to be redelivered with the same body");
        }
        finally
        {
            await SqsTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }
}
