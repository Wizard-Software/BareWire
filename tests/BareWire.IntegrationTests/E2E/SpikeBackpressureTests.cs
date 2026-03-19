using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

/// <summary>
/// E2E-2: Spike and backpressure test.
///
/// <para>
/// Runs a moderate steady-state publish rate for a short duration, then bursts a large
/// number of messages as fast as possible. Verifies that the consumer catches up after the
/// spike and that no messages are lost. Uses tight <see cref="FlowControlOptions"/> to force
/// the flow controller to activate backpressure during the spike.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class SpikeBackpressureTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
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
        string exchangeName = $"e2e-spike-ex-{suffix}";
        string queueName = $"e2e-spike-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

    private static OutboundMessage MakeMessage(string exchangeName, string queueName, int seq) =>
        new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-Seq"] = seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            body: JsonSerializer.SerializeToUtf8Bytes(
                new ThroughputMessage(
                    Id: Guid.NewGuid().ToString("N"),
                    Payload: "spike-backpressure-test",
                    SequenceNumber: seq)),
            contentType: "application/json");

    // ── E2E-2: Spike / backpressure ───────────────────────────────────────────

    /// <summary>
    /// E2E-2: Publishes a moderate steady-state batch (100 msgs), then spikes 1 000 messages
    /// as fast as possible, then verifies that the consumer catches up and all 1 100 messages
    /// are delivered with no loss.
    /// </summary>
    [Fact]
    public async Task SpikePublish_AfterSteadyState_ConsumerCatchesUpWithNoLoss()
    {
        // Arrange — 2 min total timeout (spike + catch-up grace)
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        await using RabbitMqTransportAdapter publishAdapter = CreateAdapter();
        await using RabbitMqTransportAdapter consumeAdapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) =
            await DeployTopologyAsync(publishAdapter, suffix, cts.Token);

        const int SteadyStateMessages = 100;
        const int SpikeMessages = 1_000;
        const int TotalMessages = SteadyStateMessages + SpikeMessages;

        int consumedCount = 0;

        // Use tight FlowControlOptions to trigger backpressure during spike
        // (MaxInFlightMessages=10 means 10% of the spike will immediately create back-pressure)
        var backpressureFlow = new FlowControlOptions
        {
            MaxInFlightMessages = 10,
            InternalQueueCapacity = 50,
        };

        // Start consumer before publishing so no messages are missed
        using CancellationTokenSource consumerCts =
            CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        Task consumerTask = Task.Run(async () =>
        {
            await foreach (InboundMessage msg in
                consumeAdapter.ConsumeAsync(queueName, backpressureFlow, consumerCts.Token))
            {
                await consumeAdapter.SettleAsync(SettlementAction.Ack, msg, consumerCts.Token);

                if (Interlocked.Increment(ref consumedCount) >= TotalMessages)
                {
                    await consumerCts.CancelAsync();
                    break;
                }
            }
        }, consumerCts.Token);

        // Phase 1 — steady-state: publish 100 messages at moderate rate (100 msgs/s)
        for (int i = 0; i < SteadyStateMessages; i++)
        {
            await publishAdapter.SendBatchAsync(
                [MakeMessage(exchangeName, queueName, i)],
                cts.Token);

            await Task.Delay(TimeSpan.FromMilliseconds(10), cts.Token);
        }

        // Phase 2 — spike: burst 1 000 messages as fast as possible in batches of 100
        Stopwatch spikeWatch = Stopwatch.StartNew();

        for (int batchStart = 0; batchStart < SpikeMessages; batchStart += 100)
        {
            int batchSize = Math.Min(100, SpikeMessages - batchStart);
            OutboundMessage[] batch = new OutboundMessage[batchSize];
            for (int j = 0; j < batchSize; j++)
            {
                batch[j] = MakeMessage(exchangeName, queueName, SteadyStateMessages + batchStart + j);
            }

            await publishAdapter.SendBatchAsync(batch, cts.Token);
        }

        spikeWatch.Stop();

        // Wait for consumer to catch up (outer cts provides the safety timeout)
        await consumerTask.ContinueWith(static _ => { }, TaskContinuationOptions.None);

        // Assert — no messages lost after the spike
        int finalConsumed = Volatile.Read(ref consumedCount);

        finalConsumed.Should().Be(TotalMessages,
            because: "all messages published during steady-state and spike must be consumed");

        // Assert — consumer caught up within the test timeout (implicit from no OperationCanceled)
    }
}
