using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

/// <summary>Minimal JSON payload for throughput testing (~100 bytes when serialized).</summary>
public sealed record ThroughputMessage(string Id, string Payload, long SequenceNumber);

/// <summary>
/// E2E-1: Steady-state throughput test.
///
/// <para>
/// Publishes messages at a constant rate of <see cref="MessagesPerSecond"/> for
/// <see cref="DurationSeconds"/> seconds, while a consumer loop processes and acks them.
/// Validates that the total delivered count matches the published count and that the
/// observed throughput stays within ±5% of the target.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class SteadyStateThroughputTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Constants (increase post-MVP for nightly job) ─────────────────────────

    private const int MessagesPerSecond = 1_000;
    private const int DurationSeconds = 120;
    private const int TotalExpectedMessages = MessagesPerSecond * DurationSeconds;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static async Task<(string ExchangeName, string QueueName)> DeployTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-tp-ex-{suffix}";
        string queueName = $"e2e-tp-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

    private static FlowControlOptions ThroughputFlow() =>
        new() { MaxInFlightMessages = 500, InternalQueueCapacity = 2_000 };

    // ── E2E-1: Steady-state throughput ────────────────────────────────────────

    /// <summary>
    /// E2E-1: Publishes <see cref="TotalExpectedMessages"/> at a constant rate of
    /// <see cref="MessagesPerSecond"/> msgs/s and verifies all messages are delivered
    /// and consumed within ±5% throughput tolerance.
    /// </summary>
    [Fact]
    public async Task SteadyStateThroughput_ConstantRate_AllMessagesDelivered()
    {
        // Arrange — 30 s setup + full run duration + 60 s grace for consumer catch-up
        int testTimeoutSeconds = 30 + DurationSeconds + 60;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(testTimeoutSeconds));

        await using RabbitMqTransportAdapter publishAdapter = CreateAdapter();
        await using RabbitMqTransportAdapter consumeAdapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) =
            await DeployTopologyAsync(publishAdapter, suffix, cts.Token);

        int publishedCount = 0;
        int consumedCount = 0;

        // Act — publisher loop: publish at 1K msgs/s using SendBatchAsync in 100-msg batches
        Task publisherTask = Task.Run(async () =>
        {
            // Each iteration publishes a batch of 100 messages and waits to maintain rate.
            const int BatchSize = 100;
            int batches = TotalExpectedMessages / BatchSize;
            TimeSpan batchInterval = TimeSpan.FromMilliseconds(1_000.0 / (MessagesPerSecond / BatchSize));

            var stopwatch = Stopwatch.StartNew();

            for (int batchIndex = 0; batchIndex < batches; batchIndex++)
            {
                if (cts.IsCancellationRequested)
                {
                    break;
                }

                OutboundMessage[] batch = new OutboundMessage[BatchSize];
                for (int i = 0; i < BatchSize; i++)
                {
                    long seq = (long)batchIndex * BatchSize + i;
                    var message = new ThroughputMessage(
                        Id: Guid.NewGuid().ToString("N"),
                        Payload: "steady-state-throughput-test-payload",
                        SequenceNumber: seq);

                    byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
                    batch[i] = new OutboundMessage(
                        routingKey: queueName,
                        headers: new Dictionary<string, string>
                        {
                            ["BW-Exchange"] = exchangeName,
                            ["X-Seq"] = seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        },
                        body: body,
                        contentType: "application/json");
                }

                await publishAdapter.SendBatchAsync(batch, cts.Token);
                Interlocked.Add(ref publishedCount, BatchSize);

                // Throttle: wait until the expected time for this batch has elapsed
                TimeSpan elapsed = stopwatch.Elapsed;
                TimeSpan expected = TimeSpan.FromMilliseconds((batchIndex + 1) * batchInterval.TotalMilliseconds);
                if (expected > elapsed)
                {
                    await Task.Delay(expected - elapsed, cts.Token);
                }
            }
        }, cts.Token);

        // Act — consumer loop: consume + ack in parallel
        using CancellationTokenSource consumerCts =
            CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        Stopwatch consumeThroughputWatch = Stopwatch.StartNew();

        Task consumerTask = Task.Run(async () =>
        {
            FlowControlOptions flow = ThroughputFlow();

            await foreach (InboundMessage msg in
                consumeAdapter.ConsumeAsync(queueName, flow, consumerCts.Token))
            {
                await consumeAdapter.SettleAsync(SettlementAction.Ack, msg, consumerCts.Token);

                if (Interlocked.Increment(ref consumedCount) >= TotalExpectedMessages)
                {
                    await consumerCts.CancelAsync();
                    break;
                }
            }
        }, consumerCts.Token);

        // Wait for publisher to complete
        await publisherTask;
        double publishDurationSeconds = consumeThroughputWatch.Elapsed.TotalSeconds;

        // Wait for consumer to finish (or timeout via outer cts)
        await consumerTask.ContinueWith(static _ => { }, TaskContinuationOptions.None);

        consumeThroughputWatch.Stop();

        // Assert — all published messages were consumed
        int finalPublished = Volatile.Read(ref publishedCount);
        int finalConsumed = Volatile.Read(ref consumedCount);

        finalPublished.Should().Be(TotalExpectedMessages,
            because: "publisher must send exactly the expected number of messages");

        finalConsumed.Should().Be(TotalExpectedMessages,
            because: "all published messages must be consumed");

        // Assert — throughput within ±5% of target
        double actualThroughput = finalConsumed / publishDurationSeconds;
        double minThroughput = MessagesPerSecond * 0.95;

        actualThroughput.Should().BeGreaterThanOrEqualTo(minThroughput,
            because: $"throughput must be at least {minThroughput:F0} msgs/s (target {MessagesPerSecond} ±5%)");
    }
}
