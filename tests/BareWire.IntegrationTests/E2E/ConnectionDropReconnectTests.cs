using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

/// <summary>Represents a probe message used in reconnect tests.</summary>
public sealed record ReconnectProbeMessage(string ProbeId, string Phase);

/// <summary>
/// E2E tests for the broker connection-drop and automatic reconnect scenario.
///
/// <para>
/// Verifies that <see cref="RabbitMqTransportAdapter"/> with
/// <c>AutomaticRecoveryEnabled = true</c> resumes consumption of newly published messages
/// after all server-side connections are forcibly closed.
/// </para>
///
/// <para>
/// Drop mechanism: <c>rabbitmqctl close_all_connections</c> executed via
/// <c>docker exec</c> inside the RabbitMQ container. If Docker or the container are
/// unavailable, the test is skipped deterministically (<see cref="Assert.Skip"/>),
/// never silently green.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class ConnectionDropReconnectTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter(bool automaticRecovery = true, TimeSpan? recoveryInterval = null) =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
                AutomaticRecoveryEnabled = automaticRecovery,
                NetworkRecoveryInterval = recoveryInterval ?? TimeSpan.FromSeconds(2),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static async Task<(string ExchangeName, string QueueName)> DeploySimpleTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-rc-ex-{suffix}";
        string queueName = $"e2e-rc-q-{suffix}";

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

    private static async Task<InboundMessage> ConsumeOneAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, StandardFlow(), ct))
        {
            return msg;
        }

        throw new InvalidOperationException("The consumption stream ended before a message was delivered.");
    }

    /// <summary>
    /// Attempts to force-close all RabbitMQ connections via <c>docker exec rabbitmqctl</c>.
    /// Returns <see langword="true"/> on success; <see langword="false"/> when Docker or the
    /// container are unavailable or the command returned a non-zero exit code.
    /// </summary>
    private static bool TryCloseAllConnections(out string skipReason)
    {
        skipReason = string.Empty;

        // Step 1: find the RabbitMQ container by image
        string containerId;
        try
        {
            using var findProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps --filter ancestor=rabbitmq --filter status=running --format {{.ID}}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            findProcess.Start();
            containerId = findProcess.StandardOutput.ReadToEnd().Trim();
            findProcess.WaitForExit(10_000);

            if (findProcess.ExitCode != 0 || string.IsNullOrEmpty(containerId))
            {
                // Also try filtering by image name with tag
                using var findProcess2 = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "docker",
                        Arguments = "ps --filter ancestor=rabbitmq:management --filter status=running --format {{.ID}}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };
                findProcess2.Start();
                containerId = findProcess2.StandardOutput.ReadToEnd().Trim();
                findProcess2.WaitForExit(10_000);

                if (string.IsNullOrEmpty(containerId))
                {
                    skipReason =
                        "No running RabbitMQ container found via 'docker ps' " +
                        "(filters 'rabbitmq' and 'rabbitmq:management'). " +
                        "The reconnect test requires Docker with a RabbitMQ container — skipping deterministically.";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            skipReason =
                $"Docker unavailable or threw an exception during 'docker ps': {ex.GetType().Name}: {ex.Message}. " +
                "The reconnect test requires Docker with a RabbitMQ container — skipping deterministically.";
            return false;
        }

        // Take only the first ID (there may be multiple lines if several containers are running)
        containerId = containerId.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        // Step 2: execute rabbitmqctl close_all_connections
        try
        {
            using var execProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec {containerId} rabbitmqctl close_all_connections \"test-reconnect-drop\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            execProcess.Start();
            execProcess.WaitForExit(15_000);

            if (execProcess.ExitCode != 0)
            {
                string stderr = execProcess.StandardError.ReadToEnd();
                skipReason =
                    $"'rabbitmqctl close_all_connections' returned exit code {execProcess.ExitCode}: {stderr}. " +
                    "Cannot force-drop the adapter connection — skipping deterministically.";
                return false;
            }
        }
        catch (Exception ex)
        {
            skipReason =
                $"Exception during 'docker exec rabbitmqctl close_all_connections': " +
                $"{ex.GetType().Name}: {ex.Message}. " +
                "The reconnect test requires access to the container — skipping deterministically.";
            return false;
        }

        return true;
    }

    // ── E2E: Connection drop and automatic reconnect ──────────────────────────

    /// <summary>
    /// Verifies that the RabbitMQ adapter with <c>AutomaticRecoveryEnabled = true</c>
    /// automatically resumes consumption of new messages after all server-side connections
    /// are forcibly closed (<c>rabbitmqctl close_all_connections</c>).
    ///
    /// <para>
    /// Test phases:
    /// <list type="number">
    ///   <item>Publish and consume the first message (baseline verification before the drop).</item>
    ///   <item>
    ///     Force-close connections via <c>docker exec rabbitmqctl close_all_connections</c>.
    ///     If Docker or the container are unavailable, the test is skipped with an explicit reason.
    ///   </item>
    ///   <item>
    ///     Publish a new message after the drop; poll until it is received or the timeout
    ///     elapses — behavioural reconnect verification (no recovery-event listener).
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConnectionDrop_AdapterWithAutoRecovery_ResumesConsumptionAfterReconnect()
    {
        // Arrange — 30 s: covers the baseline phase + drop + waiting for reconnect (NetworkRecoveryInterval=2s)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // Adapter under test: auto-recovery enabled, short interval (2s) for determinism
        await using RabbitMqTransportAdapter adapter = CreateAdapter(
            automaticRecovery: true,
            recoveryInterval: TimeSpan.FromSeconds(2));

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeploySimpleTopologyAsync(adapter, suffix, cts.Token);

        // ── Phase 1: baseline — message before the drop ───────────────────────

        byte[] phase1Body = SerializeToJson(new ReconnectProbeMessage(
            ProbeId: $"PROBE-{suffix[..8].ToUpperInvariant()}",
            Phase: "before-drop"));

        OutboundMessage phase1Outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-Phase"] = "before-drop",
            },
            body: phase1Body,
            contentType: "application/json");

        await adapter.SendBatchAsync([phase1Outbound], cts.Token);
        InboundMessage phase1Received = await ConsumeOneAsync(adapter, queueName, cts.Token);

        // Baseline verification
        ReconnectProbeMessage phase1Msg = DeserializeFromSequence<ReconnectProbeMessage>(phase1Received.Body);
        phase1Msg.Phase.Should().Be("before-drop",
            because: "the baseline message must arrive before the forced drop");

        await adapter.SettleAsync(SettlementAction.Ack, phase1Received, cts.Token);
        phase1Received.Dispose();

        // ── Phase 2: forced connection drop ───────────────────────────────────

        // GAP-1: adapter._connection is private — we cannot close it directly.
        // Only option: rabbitmqctl close_all_connections via docker exec.
        // If unavailable → deterministic Skip, never silently green.
        if (!TryCloseAllConnections(out string skipReason))
        {
            Assert.Skip(skipReason);
            return;
        }

        // ── Phase 3: message after the drop — behavioural reconnect verification ──

        // Wait briefly for the drop to propagate to the adapter
        await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);

        byte[] phase2Body = SerializeToJson(new ReconnectProbeMessage(
            ProbeId: $"PROBE-{suffix[..8].ToUpperInvariant()}",
            Phase: "after-reconnect"));

        OutboundMessage phase2Outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-Phase"] = "after-reconnect",
            },
            body: phase2Body,
            contentType: "application/json");

        // Publish may require several retries while reconnecting
        bool published = false;
        for (int attempt = 0; attempt < 5 && !published; attempt++)
        {
            try
            {
                IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([phase2Outbound], cts.Token);
                if (results.Count > 0 && results[0].IsConfirmed)
                {
                    published = true;
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                }
            }
            catch (Exception)
            {
                // Adapter is reconnecting — wait and retry
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }

        published.Should().BeTrue(
            because: "an adapter with auto-recovery enabled must be able to publish a message after reconnect");

        // Poll until the post-reconnect message arrives
        InboundMessage phase2Received = await ConsumeOneAsync(adapter, queueName, cts.Token);

        try
        {
            ReconnectProbeMessage phase2Msg = DeserializeFromSequence<ReconnectProbeMessage>(phase2Received.Body);
            phase2Msg.Phase.Should().Be("after-reconnect",
                because: "an adapter with auto-recovery enabled must resume consumption of new messages " +
                         "after automatic reconnect to the RabbitMQ broker");
        }
        finally
        {
            await adapter.SettleAsync(SettlementAction.Ack, phase2Received, cts.Token);
            phase2Received.Dispose();
        }
    }
}
