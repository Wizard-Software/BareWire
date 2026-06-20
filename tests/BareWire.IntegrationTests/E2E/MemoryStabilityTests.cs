using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

/// <summary>Minimalna wiadomość JSON używana w teście stabilności pamięci (~60 bajtów po serializacji).</summary>
public sealed record MemoryStabilityProbe(string Id, int Iteration);

/// <summary>
/// Testy E2E stabilności alokacji pamięci: weryfikuje, że N iteracji
/// publish → consume → Ack → Dispose nie generuje monotonicznie rosnących alokacji,
/// co sygnalizuje brak nieograniczonego wycieku pamięci w ścieżce hot-path adaptera.
///
/// <para>
/// Metodologia pomiaru:
/// <list type="bullet">
///   <item>
///     Warmup (≥50 iteracji) przed pomiarem — pozwala JIT, tiered compilation i puli
///     <c>ArrayPool&lt;byte&gt;</c> osiągnąć stan ustalony przed pomiarem baseline.
///   </item>
///   <item>
///     Metryka: <c>GC.GetTotalAllocatedBytes(precise: true)</c> process-wide — obejmuje
///     alokacje ze wszystkich wątków (w tym wątku dispatch RabbitMQ.Client).
///     NIE używamy <c>GetAllocatedBytesForCurrentThread</c>, który nie widzi alokacji
///     na wątkach dispatchera brokera.
///   </item>
///   <item>
///     Asercja względna: alokacja drugiej połowy ≤ 2.5× alokacji pierwszej połowy.
///     Wykrywa grube/monotoniczne wycieki. Twardy budżet B/op NIE jest tu asercją
///     (niefalsyfikowalny pod współdzielonym hostem Aspire).
///   </item>
///   <item>
///     <c>GC.Collect() / WaitForPendingFinalizers()</c> przed każdym oknem pomiarowym
///     minimalizuje szum nagromadzony między iteracjami.
///   </item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class MemoryStabilityTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Stałe ─────────────────────────────────────────────────────────────────

    /// <summary>Liczba iteracji warmup przed pomiarem (obowiązkowe per PERF-1).</summary>
    private const int WarmupIterations = 50;

    /// <summary>Liczba iteracji w każdym oknie pomiarowym.</summary>
    private const int MeasuredIterationsPerWindow = 1_000;

    /// <summary>
    /// Margines tolerancji: alokacja II okna ≤ 2.5× alokacji I okna.
    /// Wartość 2.5× (per PERF-2) zapewnia odporność na hałas hosta Aspire i JIT warmup
    /// przy zachowaniu czułości na grube/monotoniczne wycieki.
    /// </summary>
    private const double LeakToleranceMultiplier = 2.5;

    // ── Helpery ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static async Task<(string ExchangeName, string QueueName)> DeploySimpleTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-mem-ex-{suffix}";
        string queueName = $"e2e-mem-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
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

        throw new InvalidOperationException("Strumień konsumpcji zakończył się przed dostarczeniem wiadomości.");
    }

    /// <summary>
    /// Wykonuje jedną iterację publish → consume → Ack → Dispose.
    /// Zwraca bufor do puli przez jawny <c>Dispose</c> po rozliczeniu (D-3).
    /// </summary>
    private static async Task RunSingleIterationAsync(
        RabbitMqTransportAdapter adapter,
        string exchangeName,
        string queueName,
        int iteration,
        CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new MemoryStabilityProbe(Id: "mem-probe", Iteration: iteration));

        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
            },
            body: body,
            contentType: "application/json");

        await adapter.SendBatchAsync([outbound], ct);
        InboundMessage received = await ConsumeOneAsync(adapter, queueName, ct);
        await adapter.SettleAsync(SettlementAction.Ack, received, ct);
        received.Dispose(); // D-3: jawny Dispose — zwraca bufor ArrayPool
    }

    // ── E2E: Stabilność alokacji pamięci ──────────────────────────────────────

    /// <summary>
    /// Weryfikuje, że ścieżka publish → consume → Ack → Dispose nie generuje monotonicznie
    /// rosnących alokacji pamięci, co sygnalizuje brak nieograniczonego wycieku w adapterze RabbitMQ.
    ///
    /// <para>
    /// Asercja: alokacja drugiej połowy iteracji (II okno) ≤ 2.5× alokacji pierwszej połowy
    /// (I okno). Próg 2.5× jest celowo liberalny, by absorbować hałas współdzielonego hosta Aspire
    /// i jednocześnie wykrywać grube wycieki liniowe/monotoniczne.
    /// </para>
    ///
    /// <para>
    /// Test pomija się deterministycznie (<see cref="Assert.Skip"/>), jeśli I okno dało ~0 bajtów
    /// alokacji (co byłoby szumem pomiarowym) — zamiast dzielić przez zero lub asertować na szumie.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MemoryAllocations_NIterations_DoNotGrowMonotonically()
    {
        // 180 s: warmup (50 iter) + 2×1000 iteracji round-trip na żywym brokerze
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(180));

        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeploySimpleTopologyAsync(adapter, suffix, cts.Token);

        // ── Warmup: JIT, tiered compilation, ArrayPool fill ───────────────────

        for (int i = 0; i < WarmupIterations; i++)
        {
            await RunSingleIterationAsync(adapter, exchangeName, queueName, iteration: i, cts.Token);
        }

        // ── I okno pomiarowe ──────────────────────────────────────────────────

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocBefore1 = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < MeasuredIterationsPerWindow; i++)
        {
            await RunSingleIterationAsync(adapter, exchangeName, queueName, iteration: WarmupIterations + i, cts.Token);
        }

        long allocAfter1 = GC.GetTotalAllocatedBytes(precise: true);
        long window1Delta = allocAfter1 - allocBefore1;

        // ── II okno pomiarowe ─────────────────────────────────────────────────

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocBefore2 = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < MeasuredIterationsPerWindow; i++)
        {
            await RunSingleIterationAsync(
                adapter, exchangeName, queueName,
                iteration: WarmupIterations + MeasuredIterationsPerWindow + i,
                cts.Token);
        }

        long allocAfter2 = GC.GetTotalAllocatedBytes(precise: true);
        long window2Delta = allocAfter2 - allocBefore2;

        // ── Asercja ───────────────────────────────────────────────────────────

        // Jeśli I okno dało ~0 (szum pomiarowy poniżej rozdzielczości licznika),
        // nie możemy sensownie obliczyć progu — pomijamy deterministycznie.
        const long MinMeaningfulDeltaBytes = 4096; // 4 KB: minimalna sensowna alokacja
        if (window1Delta < MinMeaningfulDeltaBytes)
        {
            Assert.Skip(
                $"I okno alokacji ({window1Delta} B) jest poniżej progu szumu pomiarowego " +
                $"({MinMeaningfulDeltaBytes} B). Pomiar process-wide GC.GetTotalAllocatedBytes " +
                "może być zbyt zaszumiony przez hosta Aspire, by dać sensowny wynik. " +
                "Test pomijamy deterministycznie — nigdy cicho-zielony.");
            return;
        }

        long threshold = (long)(window1Delta * LeakToleranceMultiplier);

        window2Delta.Should().BeLessThanOrEqualTo(threshold,
            because:
                $"II okno ({MeasuredIterationsPerWindow} iter, {window2Delta:N0} B) " +
                $"nie może przekroczyć {LeakToleranceMultiplier}× alokacji I okna " +
                $"({MeasuredIterationsPerWindow} iter, {window1Delta:N0} B, próg: {threshold:N0} B). " +
                "Przekroczenie sygnalizuje nieograniczony/monotoniczny wzrost alokacji w ścieżce " +
                "publish→consume→Ack→Dispose adaptera RabbitMQ, co wskazuje na wyciek pamięci. " +
                "Twardy budżet B/op jest weryfikowany przez izolowany benchmark BenchmarkDotNet.");
    }
}
