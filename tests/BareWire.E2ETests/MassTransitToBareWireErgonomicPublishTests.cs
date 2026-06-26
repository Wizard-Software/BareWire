using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using RabbitMQ.Client;
using Xunit;

namespace BareWire.E2ETests;

/// <summary>
/// Bounded E2E smoke test for the <c>BareWire.Samples.MassTransitToBareWire</c> console sample
/// after its migration to the ergonomic per-type publish API (ADR-029).
///
/// <para>
/// The sample is a standalone one-shot console app (not an AppHost-hosted web sample), so this
/// test reuses <see cref="SamplesAppFixture"/> only for a healthy RabbitMQ broker, then launches
/// the sample as a subprocess pointed at that broker via <c>RABBITMQ_CONNECTIONSTRING</c>.
/// </para>
///
/// <para>
/// What is asserted, and why it proves the ergonomic mapping drives publishing:
/// <list type="number">
///   <item><description>
///     The subprocess exits with code 0 — the MT-&gt;BareWire request/response round-trip still
///     works end-to-end (behaviour preserved). Because the sample sets <em>no</em> DefaultExchange,
///     a clean exit also proves <c>PublishAsync&lt;InventoryChecked&gt;</c> resolved a target exchange
///     purely from the per-type mapping registered by <c>DeclareExchange&lt;InventoryChecked&gt;</c>
///     (otherwise publish would throw and the process would exit non-zero).
///   </description></item>
///   <item><description>
///     A message arrives on an observer queue bound to <c>bw-inventory-events</c> with the EXACT
///     routing key <c>inventory.checked</c>. Topic-exchange routing only matches when the published
///     message carries that routing key — so arrival proves the ergonomic routing-key mapping (not
///     the <c>typeof(T).FullName</c> default) drove the publish.
///   </description></item>
///   <item><description>
///     The body is raw JSON (ADR-001) carrying the processed inventory result.
///   </description></item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "requires-rabbitmq")]
public sealed class MassTransitToBareWireErgonomicPublishTests(SamplesAppFixture fixture)
    : IClassFixture<SamplesAppFixture>
{
    // Must match the ergonomic DeclareExchange<InventoryChecked>(...) call in the sample's Program.cs.
    private const string EventsExchange = "bw-inventory-events";
    private const string EventsRoutingKey = "inventory.checked";

    private static readonly TimeSpan SampleRunTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public async Task SmokeTest_ErgonomicMapping_RoutesInventoryCheckedToConfiguredExchangeAndRoutingKey()
    {
        using var cts = new CancellationTokenSource(SampleRunTimeout);

        // ── Arrange: bind an observer queue to the ergonomic-mapped exchange BEFORE the sample
        //    publishes. The exchange is declared with the SAME attributes the sample uses
        //    (topic, durable, non-auto-delete) so the redeclare inside the sample is idempotent. ──
        var factory = new ConnectionFactory
        {
            Uri = new Uri(fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };

        await using IConnection connection = await factory.CreateConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        await channel.ExchangeDeclareAsync(
            exchange: EventsExchange,
            type: RabbitMQ.Client.ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cts.Token);

        string observerQueue = $"e2e-inventory-events-observer-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(
            queue: observerQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            cancellationToken: cts.Token);

        // Bind with the EXACT routing key — arrival on this queue proves the mapping's routing key.
        await channel.QueueBindAsync(
            queue: observerQueue,
            exchange: EventsExchange,
            routingKey: EventsRoutingKey,
            cancellationToken: cts.Token);

        // ── Act: run the migrated sample end-to-end against the fixture's broker. ──
        (int exitCode, string stdout, string stderr) = await RunSampleAsync(
            fixture.GetRabbitMqConnectionString(), cts.Token);

        // ── Assert (1): the round-trip ran and the ergonomic publish resolved an exchange. ──
        exitCode.Should().Be(
            0,
            $"the migrated sample must run end-to-end and publish successfully.\n" +
            $"--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}");

        // ── Assert (2): the InventoryChecked event landed on bw-inventory-events / inventory.checked. ──
        BasicGetResult? delivery = await PollForMessageAsync(channel, observerQueue, cts.Token);

        delivery.Should().NotBeNull(
            "the ergonomic per-type mapping must route InventoryChecked to the " +
            $"'{EventsExchange}' topic exchange with routing key '{EventsRoutingKey}'");

        delivery!.RoutingKey.Should().Be(
            EventsRoutingKey,
            "the published routing key must come from the ergonomic mapping, not the typeof(T).FullName default");
        delivery.Exchange.Should().Be(EventsExchange);
        delivery.BasicProperties.ContentType.Should().Be(
            "application/json",
            "InventoryChecked is published with the default raw serializer (ADR-001 raw-first)");

        // ── Assert (3): the body carries a processed inventory result (raw JSON). ──
        string json = Encoding.UTF8.GetString(delivery.Body.ToArray());
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("sku").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("processedBy").GetString().Should().Be("BareWire/InventoryConsumer");
        root.TryGetProperty("available", out JsonElement available).Should().BeTrue();
        available.ValueKind.Should().Be(JsonValueKind.Number);

        await channel.QueueUnbindAsync(
            observerQueue, EventsExchange, EventsRoutingKey, cancellationToken: cts.Token);
    }

    /// <summary>
    /// Launches the built <c>BareWire.Samples.MassTransitToBareWire</c> sample as a subprocess,
    /// pointed at <paramref name="rabbitMqConnectionString"/>, and waits for it to exit.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunSampleAsync(
        string rabbitMqConnectionString,
        CancellationToken cancellationToken)
    {
        string sampleDll = ResolveSampleDllPath();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.Environment["RABBITMQ_CONNECTIONSTRING"] = rabbitMqConnectionString;

        using var process = new Process { StartInfo = psi };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup; the process may have exited between the check and the kill.
        }
    }

    /// <summary>
    /// Resolves the path to the sample's built assembly. The sample is built (but not referenced)
    /// via a <c>ReferenceOutputAssembly=false</c> ProjectReference in this test project, so it is
    /// guaranteed to exist in the matching build configuration.
    /// </summary>
    private static string ResolveSampleDllPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BareWire.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                $"Could not locate the repository root (BareWire.slnx) from '{AppContext.BaseDirectory}'.");
        }

        string dll = Path.Combine(
            dir.FullName,
            "samples",
            "BareWire.Samples.MassTransitToBareWire",
            "bin",
            BuildConfiguration,
            "net10.0",
            "BareWire.Samples.MassTransitToBareWire.dll");

        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"Sample assembly not found at '{dll}'. Ensure the ProjectReference builds it.", dll);
        }

        return dll;
    }

    private static async Task<BasicGetResult?> PollForMessageAsync(
        IChannel channel,
        string queueName,
        CancellationToken cancellationToken)
    {
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pollCts.CancelAfter(PollTimeout);

        TimeSpan interval = TimeSpan.FromMilliseconds(250);

        while (!pollCts.IsCancellationRequested)
        {
            BasicGetResult? result =
                await channel.BasicGetAsync(queueName, autoAck: true, pollCts.Token);
            if (result is not null)
            {
                return result;
            }

            try
            {
                await Task.Delay(interval, pollCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }
}
