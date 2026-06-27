using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using RabbitMQ.Client;
using Xunit;

namespace BareWire.E2ETests.MassTransitToBareWire;

/// <summary>
/// Bounded E2E smoke test for the <c>BareWire.Samples.MassTransitToBareWire</c> console sample
/// after it was extended into a per-consumer mixed-format demo: two consumers on one queue, where
/// <c>InventoryConsumer</c> opts into the MassTransit envelope (<c>UseMassTransitEnvelope()</c>)
/// and <c>ShipmentConsumer</c> stays raw-first.
///
/// <para>
/// The sample is a standalone one-shot console app (not an AppHost-hosted web sample), so this
/// test reuses <see cref="SamplesAppFixture"/> only for a healthy RabbitMQ broker, then launches
/// the sample as a subprocess pointed at that broker via <c>RABBITMQ_CONNECTIONSTRING</c>.
/// </para>
///
/// <para>
/// What is asserted, and why it proves mixed per-consumer formats on one queue:
/// <list type="number">
///   <item><description>
///     The subprocess exits with code 0. The sample's driver awaits the MassTransit
///     <c>IRequestClient&lt;CheckInventory&gt;</c> response; that only completes when the
///     envelope consumer read the inbound MT request envelope AND replied with a conformant MT
///     response envelope (correlated by <c>requestId</c>). A clean exit therefore proves the
///     per-consumer envelope path (receive + reply) worked. The driver also awaits the raw round,
///     so exit 0 additionally proves the raw consumer ran.
///   </description></item>
///   <item><description>
///     A <c>ShipmentRecorded</c> message arrives on an observer queue bound to
///     <c>bw-shipment-events</c> / <c>shipment.recorded</c> as <em>raw</em> JSON
///     (<c>application/json</c>). This is emitted by the raw <c>ShipmentConsumer</c> after it
///     consumed a raw <c>ShipmentNotice</c> from the SAME queue the envelope consumer uses — so
///     arrival proves the raw consumer coexisted with the envelope consumer on one endpoint.
///   </description></item>
///   <item><description>
///     The body carries the processed shipment (raw JSON: <c>sku</c> + <c>quantity</c>),
///     proving raw-first deserialization (not envelope unwrapping) drove the raw path.
///   </description></item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "requires-rabbitmq")]
public sealed class MassTransitToBareWirePerConsumerEnvelopeTests(SamplesAppFixture fixture)
    : IClassFixture<SamplesAppFixture>
{
    // Must match the raw-output mapping declared in the sample's Program.cs.
    private const string ShipmentEventsExchange = "bw-shipment-events";
    private const string ShipmentRecordedRoutingKey = "shipment.recorded";

    private static readonly TimeSpan SampleRunTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public async Task SmokeTest_MixedConsumers_EnvelopeAndRawShareOneQueue()
    {
        using var cts = new CancellationTokenSource(SampleRunTimeout);

        // ── Arrange: bind an observer queue to the raw-output exchange BEFORE the sample runs.
        //    Declared with the SAME attributes the sample uses (topic, durable, non-auto-delete)
        //    so the sample's redeclare is idempotent (no PRECONDITION_FAILED). ──
        var factory = new ConnectionFactory
        {
            Uri = new Uri(fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };

        await using IConnection connection = await factory.CreateConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        await channel.ExchangeDeclareAsync(
            exchange: ShipmentEventsExchange,
            type: RabbitMQ.Client.ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cts.Token);

        string observerQueue = $"e2e-shipment-events-observer-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(
            queue: observerQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            cancellationToken: cts.Token);

        await channel.QueueBindAsync(
            queue: observerQueue,
            exchange: ShipmentEventsExchange,
            routingKey: ShipmentRecordedRoutingKey,
            cancellationToken: cts.Token);

        // ── Act: run the extended sample end-to-end against the fixture's broker. ──
        (int exitCode, string stdout, string stderr) = await RunSampleAsync(
            fixture.GetRabbitMqConnectionString(), cts.Token);

        // ── Assert (1): clean exit proves the MT-envelope consumer round-trip (receive + reply)
        //    AND the raw round both completed (the driver awaits both). ──
        exitCode.Should().Be(
            0,
            $"the mixed-consumer sample must run end-to-end: the per-consumer MassTransit-envelope " +
            $"round-trip (requestId-correlated reply) and the raw round must both succeed.\n" +
            $"--- STDOUT ---\n{stdout}\n--- STDERR ---\n{stderr}");

        // ── Assert (2): the raw consumer ran on the same queue and emitted a raw ShipmentRecorded. ──
        BasicGetResult? delivery = await PollForMessageAsync(channel, observerQueue, cts.Token);

        delivery.Should().NotBeNull(
            "the raw ShipmentConsumer must consume the raw ShipmentNotice on the shared queue and " +
            $"publish ShipmentRecorded to '{ShipmentEventsExchange}' / '{ShipmentRecordedRoutingKey}'");

        delivery!.RoutingKey.Should().Be(ShipmentRecordedRoutingKey);
        delivery.Exchange.Should().Be(ShipmentEventsExchange);
        delivery.BasicProperties.ContentType.Should().Be(
            "application/json",
            "ShipmentRecorded is published raw-first (no MassTransit envelope) by the raw consumer");

        // ── Assert (3): the body is the raw processed shipment (no envelope wrapping). ──
        string json = Encoding.UTF8.GetString(delivery.Body.ToArray());
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("sku").GetString().Should().Be("SKU-001");
        root.GetProperty("processedBy").GetString().Should().Be("BareWire/ShipmentConsumer");
        root.TryGetProperty("quantity", out JsonElement quantity).Should().BeTrue();
        quantity.ValueKind.Should().Be(JsonValueKind.Number);
        quantity.GetInt32().Should().Be(7);

        await channel.QueueUnbindAsync(
            observerQueue, ShipmentEventsExchange, ShipmentRecordedRoutingKey, cancellationToken: cts.Token);
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
