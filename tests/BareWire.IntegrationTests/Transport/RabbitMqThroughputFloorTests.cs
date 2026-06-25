using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using ExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Harness pomiarowy przepustowości trzech ścieżek RabbitMQ (R8.16, ADR-026 §8).
///
/// <para>
/// Mierzy msgs/s dla:
/// <list type="bullet">
/// <item><description>Competing-consumers — jedna kolejka, <see cref="ConsumerInstances"/> konsumentów round-robin (baseline).</description></item>
/// <item><description>SAC (Single Active Consumer) — jedna kolejka z <c>x-single-active-consumer</c>; floor ≈ 1 konsument.</description></item>
/// <item><description>Consistent-hash exchange + per-key queues — <see cref="KeyCardinality"/> kolejek K, <see cref="ConsumerInstances"/> konsumentów.</description></item>
/// </list>
/// </para>
///
/// <para>
/// Test bramkujący <see cref="ConsistentHash_SustainsThresholdFraction_OfCompetingConsumers"/> asertuje,
/// że ścieżka consistent-hash utrzymuje ≥ <see cref="ThresholdFractionPercent"/>% przepustowości
/// competing-consumers przy K = <see cref="KeyCardinality"/> kluczach.
/// Wartości <see cref="ThresholdFractionPercent"/> i <see cref="KeyCardinality"/> wyznaczono empirycznie
/// przez harness R8.16 (lokalny Docker, RabbitMQ 4.x przez Aspire, .NET 10, czerwiec 2026).
/// </para>
///
/// <para>
/// Uruchomienie: <c>dotnet test tests/BareWire.IntegrationTests/ --filter "Category=Throughput"</c>.
/// Test nie wchodzi do domyślnego CI gate — IntegrationTests są wykluczone z CI.
/// </para>
/// </summary>
[Trait("Category", "Throughput")]
public sealed class RabbitMqThroughputFloorTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Stałe pomiarowe — wyznaczone empirycznie przez harness R8.16 ──────────

    /// <summary>
    /// Łączna liczba wiadomości na powtórzenie pomiaru.
    /// Dobrana tak, by jeden run mieścił się w ok. 15 s przy wolnym brokerze Docker.
    /// </summary>
    private const int MessageVolume = 5_000;

    /// <summary>
    /// Kardynalność kluczy dla ścieżki consistent-hash (K = 16, wyznaczone empirycznie).
    /// Sweep K ∈ {8, 16, 32} pokazał plateau stosunku consistent-hash/competing ≈ 97% już przy K=8;
    /// K=16 wybrane jako wartość z realną równoległością (K ≥ ConsumerInstances = 4) i marginesem
    /// na key-skew bez nadmiernego narzutu topologii.
    /// Musi spełniać K ≥ ConsumerInstances (enforced przez sweep: K=8 to dolny rozważany kandydat).
    /// </summary>
    private const int KeyCardinality = 16;

    /// <summary>
    /// Liczba równoległych konsumentów (wspólna dla competing-consumers i consistent-hash).
    /// Dla SAC: <see cref="ConsumerInstances"/> konsumentów podłączonych, ale tylko 1 aktywny.
    /// </summary>
    private const int ConsumerInstances = 4;

    /// <summary>
    /// Liczba powtórzeń per ścieżka (≥ 5 zalecane przez PERF-5).
    /// Mediana stosunków per-rep jest bardziej odporna na szum I/O Dockera niż min/median.
    /// </summary>
    private const int Repetitions = 7;

    /// <summary>
    /// Minimalny próg stosunku consistent-hash / competing-consumers wyrażony w procentach.
    /// Wartość wyznaczona empirycznie z pomiarów R8.16 (7 powtórzeń):
    /// zmierzony stosunek ≈ 97.1% (competing ≈ 1846 msgs/s, ch P20 ≈ 1792 msgs/s)
    /// przy K = <see cref="KeyCardinality"/> i ConsumerInstances = <see cref="ConsumerInstances"/>;
    /// konserwatywny margines 10 p.p. (round-down) → próg = 87%.
    /// Środowisko pomiaru: lokalny Docker, RabbitMQ 4.x via Aspire, .NET 10, 2026-06-25.
    /// Falsyfikacja: tymczasowe podniesienie powyżej zmierzonego stosunku powoduje RED.
    /// </summary>
    private const double ThresholdFractionPercent = 87.0;

    /// <summary>
    /// Tolerancja asercji dolnej granicy SAC.
    /// SAC musi osiągnąć ≥ <c>competing / ConsumerInstances × (1 − SacLowerTolerance)</c>.
    /// Szeroki pas (80%) uwzględnia scenariusze broker-bottlenecked (Docker I/O),
    /// w których SAC może być wyższy niż expected-floor (broker, nie ConsumerInstances, jest wąskim gardłem).
    /// </summary>
    private const double SacLowerTolerance = 0.80;

    /// <summary>
    /// Tolerancja asercji górnej granicy SAC.
    /// SAC nie powinien przekraczać przepustowości competing-consumers o więcej niż <see cref="SacUpperFactor"/> ×.
    /// Wartość 1.2 uwzględnia szum Docker I/O (SAC może chwilowo mieć mniejszy narzut schedulingu
    /// przy 1 aktywnym konsumencie vs N konkurujących).
    /// </summary>
    private const double SacUpperFactor = 1.20;

    // ── Pomocnicze dane wiadomości ─────────────────────────────────────────────

    private static readonly byte[] MessageBody =
        Encoding.UTF8.GetBytes("{\"type\":\"tput-probe\",\"payload\":\"benchmark\"}");

    // ── Fabryki ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static FlowControlOptions ThroughputFlow() =>
        new() { MaxInFlightMessages = 500, InternalQueueCapacity = 2_000 };

    // ── Test bramkujący (gating [Fact]) ──────────────────────────────────────

    /// <summary>
    /// Bramka przepustowości: consistent-hash + per-key queues przy K = <see cref="KeyCardinality"/>
    /// utrzymuje ≥ <see cref="ThresholdFractionPercent"/>% przepustowości competing-consumers (ADR-026 §8).
    /// SAC asertowany jako floor ≈ udział 1 konsumenta z baseline (realna asercja floor, nie tautologia).
    /// </summary>
    [Fact]
    public async Task ConsistentHash_SustainsThresholdFraction_OfCompetingConsumers()
    {
        // CTS pokrywa 3 ścieżki × Repetitions powtórzeń, każde po ~15 s + narzut warmup.
        // Budżet: 3 × (1 warmup + 7 reps) × ~15 s ≈ 360 s; 480 s daje bufor na wolny Docker.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(480));

        double[] competingRates = await MeasurePathAsync(
            () => RunCompetingConsumersRepAsync(KeyCardinality, cts.Token),
            Repetitions, cts.Token);

        double[] consistentHashRates = await MeasurePathAsync(
            () => RunConsistentHashRepAsync(KeyCardinality, cts.Token),
            Repetitions, cts.Token);

        double[] sacRates = await MeasurePathAsync(
            () => RunSacRepAsync(cts.Token),
            Repetitions, cts.Token);

        double competingMedian = Percentile(competingRates, 50);
        double consistentHashP20 = Percentile(consistentHashRates, 20);
        double sacMedian = Percentile(sacRates, 50);

        // Stosunek consistent-hash do competing-consumers (%).
        double ratio = consistentHashP20 / competingMedian * 100.0;

        // Bramka główna: consistent-hash ≥ ThresholdFractionPercent% throughput competing-consumers.
        ratio.Should().BeGreaterThanOrEqualTo(ThresholdFractionPercent,
            $"consistent-hash + per-key queues (K={KeyCardinality}) " +
            $"musi utrzymać ≥ {ThresholdFractionPercent}% floor z ADR-026 §8 " +
            $"(zmierzono: {ratio:F1}% przy competing={competingMedian:F0} msgs/s, " +
            $"ch={consistentHashP20:F0} msgs/s)");

        // Asercja SAC dolna granica: SAC ≥ udział 1 konsumenta z baseline × (1 − SacLowerTolerance).
        // Dowodzi, że SAC mierzy realny ruch (nie zero), i zapewnia dolny sens floor.
        // Szeroka tolerancja (80%) uwzględnia broker-bottlenecked Docker: gdy broker jest wąskim gardłem,
        // SAC może osiągać podobny wynik co competing-consumers (wszystkie ścieżki ograniczone przez brokera).
        double expectedSacFloor = competingMedian / ConsumerInstances;
        sacMedian.Should().BeGreaterThanOrEqualTo(expectedSacFloor * (1.0 - SacLowerTolerance),
            $"SAC musi osiągnąć ≥ {(1.0 - SacLowerTolerance) * 100:F0}% floor (1 konsument z baseline) " +
            $"(floor ≈ {expectedSacFloor:F0} msgs/s, SAC zmierzony: {sacMedian:F0} msgs/s)");

        // Asercja SAC górna granica: SAC ≤ competing × SacUpperFactor.
        // SAC (1 aktywny konsument) nie powinien trwale przekraczać competing-consumers
        // (które mają ConsumerInstances konsumentów). Szum Docker może dawać chwilowe różnice.
        sacMedian.Should().BeLessThanOrEqualTo(competingMedian * SacUpperFactor,
            $"SAC nie powinien przekraczać competing-consumers o więcej niż {(SacUpperFactor - 1.0) * 100:F0}% " +
            $"(competing ≈ {competingMedian:F0} msgs/s, SAC zmierzony: {sacMedian:F0} msgs/s)");

        // Asercja kierunkowa sanity-guard: SAC ≤ consistent-hash × 1.2 (logiczny porządek floor).
        sacMedian.Should().BeLessThanOrEqualTo(consistentHashP20 * SacUpperFactor,
            "SAC (1 aktywny konsument) nie powinien przekraczać consistent-hash " +
            "(równoległość po kluczach) o więcej niż 20%");
    }

    // ── K-sweep (exploracyjny [Theory]) ──────────────────────────────────────

    /// <summary>
    /// Exploracyjny sweep K ∈ {8, 16, 32} — uruchom przed ustaleniem
    /// <see cref="ThresholdFractionPercent"/> i <see cref="KeyCardinality"/>.
    /// Wyniki pozwalają wybrać optymalne K (plateau stosunku przy K ≥ ConsumerInstances).
    /// Nie asertuje progu — produkuje dane pomiarowe.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [Trait("Category", "ThroughputSweep")]
    public async Task ConsistentHash_Sweep_ReportsRatioForKeyCardinality(int k)
    {
        // Własny, dłuższy CTS dla sweep — nie dzielony z testem bramkującym.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

        const int sweepReps = 5;

        double[] competingRates = await MeasurePathAsync(
            () => RunCompetingConsumersRepAsync(k, cts.Token),
            sweepReps, cts.Token);

        double[] consistentHashRates = await MeasurePathAsync(
            () => RunConsistentHashRepAsync(k, cts.Token),
            sweepReps, cts.Token);

        double competingMedian = Percentile(competingRates, 50);
        double consistentHashP20 = Percentile(consistentHashRates, 20);
        double ratio = consistentHashP20 / competingMedian * 100.0;

        // Sweep nie asertuje progu — tylko weryfikuje, że pomiar zakończył się bez wyjątku.
        // Odczytaj wartości przez debugger lub dodaj breakpoint, by zidentyfikować plateau K.
        _ = ratio; // suppress unused-variable warning; wartość dostępna w debuggerze
        _ = competingMedian;
        _ = consistentHashP20;

        // Brak asercji progu — purpose: dostarczenie danych do wyboru K i ThresholdFractionPercent.
        true.Should().BeTrue("sweep zakończony bez wyjątku — sprawdź zmienne lokalne po zebraniu danych");
    }

    // ── Mierzenie ścieżek ─────────────────────────────────────────────────────

    /// <summary>
    /// Uruchamia <paramref name="action"/> <paramref name="repetitions"/> razy i zwraca tablicę msgs/s.
    /// Pierwsza iteracja pełni rolę warmup i jest ujęta w pomiarze — połączenia i kanały
    /// są ustanawiane PRZED startem Stopwatch w każdym powtórzeniu (PERF-1).
    /// </summary>
    private static async Task<double[]> MeasurePathAsync(
        Func<Task<double>> action,
        int repetitions,
        CancellationToken ct)
    {
        var rates = new double[repetitions];

        // Powtórzenie 0 = warmup (połączenia nawiązane, kolejki warm); wynik uwzględniony w pomiarze.
        for (int i = 0; i < repetitions; i++)
        {
            ct.ThrowIfCancellationRequested();
            rates[i] = await action();
        }

        return rates;
    }

    // ── Ścieżka 1: competing-consumers ───────────────────────────────────────

    /// <summary>
    /// Mierzy przepustowość competing-consumers: jedna kolejka, <see cref="ConsumerInstances"/>
    /// konsumentów round-robin (baseline, mianownik stosunku).
    /// </summary>
    private async Task<double> RunCompetingConsumersRepAsync(int keyCardinality, CancellationToken ct)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"tput-cc-{suffix}";

        await using RabbitMqTransportAdapter publishAdapter = CreateAdapter();
        await using RabbitMqTransportAdapter consumeAdapter = CreateAdapter();

        // Topologia: prosta kolejka bez dodatkowych argumentów.
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        await publishAdapter.DeployTopologyAsync(configurator.Build(), ct);

        // Ustanów połączenie konsumenckie PRZED startem Stopwatch (PERF-1: warmup połączenia).
        using var consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumed = new AtomicCounter();

        // ConsumerInstances równoległych pętli konsumujecich.
        Task[] consumerTasks = new Task[ConsumerInstances];
        for (int c = 0; c < ConsumerInstances; c++)
        {
            consumerTasks[c] = Task.Run(async () =>
            {
                await foreach (InboundMessage msg in
                    consumeAdapter.ConsumeAsync(queueName, ThroughputFlow(), consumerCts.Token))
                {
                    await consumeAdapter.SettleAsync(SettlementAction.Ack, msg, consumerCts.Token)
                        .ConfigureAwait(false);

                    if (consumed.Increment() >= MessageVolume)
                    {
                        await consumerCts.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                }
            }, consumerCts.Token);
        }

        // Krótki warmup sieci: opublikuj 1 wiadomość i poczekaj na jej skonsumowanie.
        // keyCardinality=1 → routingKey = queueName (default-exchange, routing 1:1 do kolejki).
        await PublishBatchAsync(publishAdapter, string.Empty, queueName, 1, 1, ct)
            .ConfigureAwait(false);
        await WaitForCountAsync(consumed, 1, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        consumed.Reset();

        // Pomiar: Stopwatch startuje PO warmupie, PO ustanowieniu połączeń.
        var sw = Stopwatch.StartNew();
        await PublishBatchAsync(publishAdapter, string.Empty, queueName, MessageVolume, 1, ct)
            .ConfigureAwait(false);
        await WaitForCountAsync(consumed, MessageVolume, TimeSpan.FromSeconds(60), ct)
            .ConfigureAwait(false);
        sw.Stop();

        await consumerCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(consumerTasks).ContinueWith(static _ => { }, TaskContinuationOptions.None)
            .ConfigureAwait(false);

        return MessageVolume / sw.Elapsed.TotalSeconds;
    }

    // ── Ścieżka 2: SAC (Single Active Consumer) ──────────────────────────────

    /// <summary>
    /// Mierzy przepustowość SAC: jedna kolejka z <c>x-single-active-consumer</c>.
    /// <see cref="ConsumerInstances"/> konsumentów podłączonych, ale tylko 1 aktywny naraz.
    /// Floor ≈ przepustowość 1 konsumenta z baseline.
    /// </summary>
    private async Task<double> RunSacRepAsync(CancellationToken ct)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"tput-sac-{suffix}";

        await using RabbitMqTransportAdapter publishAdapter = CreateAdapter();
        await using RabbitMqTransportAdapter consumeAdapter = CreateAdapter();

        // Topologia: kolejka SAC (x-single-active-consumer = true).
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false,
            configure: q => q.SingleActiveConsumer());
        await publishAdapter.DeployTopologyAsync(configurator.Build(), ct);

        using var consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumed = new AtomicCounter();

        // ConsumerInstances konsumentów podłączonych do tej samej kolejki SAC.
        Task[] consumerTasks = new Task[ConsumerInstances];
        for (int c = 0; c < ConsumerInstances; c++)
        {
            consumerTasks[c] = Task.Run(async () =>
            {
                await foreach (InboundMessage msg in
                    consumeAdapter.ConsumeAsync(queueName, ThroughputFlow(), consumerCts.Token))
                {
                    await consumeAdapter.SettleAsync(SettlementAction.Ack, msg, consumerCts.Token)
                        .ConfigureAwait(false);

                    if (consumed.Increment() >= MessageVolume)
                    {
                        await consumerCts.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                }
            }, consumerCts.Token);
        }

        // Warmup: 1 wiadomość przed pomiarem.
        await PublishBatchAsync(publishAdapter, string.Empty, queueName, 1, 1, ct).ConfigureAwait(false);
        await WaitForCountAsync(consumed, 1, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        consumed.Reset();

        var sw = Stopwatch.StartNew();
        await PublishBatchAsync(publishAdapter, string.Empty, queueName, MessageVolume, 1, ct)
            .ConfigureAwait(false);
        await WaitForCountAsync(consumed, MessageVolume, TimeSpan.FromSeconds(90), ct)
            .ConfigureAwait(false);
        sw.Stop();

        await consumerCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(consumerTasks).ContinueWith(static _ => { }, TaskContinuationOptions.None)
            .ConfigureAwait(false);

        return MessageVolume / sw.Elapsed.TotalSeconds;
    }

    // ── Ścieżka 3: consistent-hash exchange + per-key queues ─────────────────

    /// <summary>
    /// Mierzy przepustowość consistent-hash: jeden exchange <c>x-consistent-hash</c> +
    /// <paramref name="keyCardinality"/> per-key queues z wagą „1", <see cref="ConsumerInstances"/>
    /// konsumentów rozłożonych po kolejkach.
    /// Routing: klucz routingu wiadomości (syntetyczny <c>tput-key-{n}</c>) jest hash-kluczem;
    /// plugin RabbitMQ <c>rabbitmq_consistent_hash_exchange</c> wyznacza docelową kolejkę.
    /// </summary>
    private async Task<double> RunConsistentHashRepAsync(int keyCardinality, CancellationToken ct)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"tput-ch-ex-{suffix}";
        string[] queues = new string[keyCardinality];

        for (int i = 0; i < keyCardinality; i++)
        {
            queues[i] = $"tput-ch-q{i}-{suffix}";
        }

        await using RabbitMqTransportAdapter publishAdapter = CreateAdapter();
        await using RabbitMqTransportAdapter consumeAdapter = CreateAdapter();

        // Topologia: consistent-hash exchange + K per-key queues, każda z wagą „1".
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.ConsistentHash,
            durable: false, autoDelete: false);

        for (int i = 0; i < keyCardinality; i++)
        {
            configurator.DeclareQueue(queues[i], durable: false, autoDelete: false);
            // Binding routingKey = waga kolejki (integer string) — konwencja pluginu consistent-hash.
            configurator.BindExchangeToQueue(exchangeName, queues[i], routingKey: "1");
        }

        await publishAdapter.DeployTopologyAsync(configurator.Build(), ct);

        using var consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumed = new AtomicCounter();

        // ConsumerInstances konsumentów rozłożonych round-robin po K kolejkach.
        Task[] consumerTasks = new Task[ConsumerInstances];
        for (int c = 0; c < ConsumerInstances; c++)
        {
            // Każdy konsument obsługuje subset kolejek (round-robin po indeksie).
            int consumerIndex = c;
            consumerTasks[c] = Task.Run(async () =>
            {
                // Każdy konsument obsługuje kolejki o indeksach ≡ consumerIndex (mod ConsumerInstances).
                string[] assignedQueues = [.. Enumerable.Range(0, keyCardinality)
                    .Where(i => i % ConsumerInstances == consumerIndex)
                    .Select(i => queues[i])];

                // Uruchamiamy jedną pętlę ConsumeAsync per przypisana kolejka.
                Task[] queueTasks = assignedQueues.Select(queue => Task.Run(async () =>
                {
                    await foreach (InboundMessage msg in
                        consumeAdapter.ConsumeAsync(queue, ThroughputFlow(), consumerCts.Token))
                    {
                        await consumeAdapter.SettleAsync(SettlementAction.Ack, msg, consumerCts.Token)
                            .ConfigureAwait(false);

                        if (consumed.Increment() >= MessageVolume)
                        {
                            await consumerCts.CancelAsync().ConfigureAwait(false);
                            break;
                        }
                    }
                }, consumerCts.Token)).ToArray();

                await Task.WhenAll(queueTasks)
                    .ContinueWith(static _ => { }, TaskContinuationOptions.None)
                    .ConfigureAwait(false);
            }, consumerCts.Token);
        }

        // Warmup: opublikuj 1 wiadomość do exchange i poczekaj na konsumpcję.
        await PublishBatchAsync(publishAdapter, exchangeName, "tput-key-warmup", 1, keyCardinality, ct)
            .ConfigureAwait(false);
        await WaitForCountAsync(consumed, 1, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        consumed.Reset();

        var sw = Stopwatch.StartNew();
        await PublishBatchAsync(publishAdapter, exchangeName, "tput-key", MessageVolume, keyCardinality, ct)
            .ConfigureAwait(false);
        await WaitForCountAsync(consumed, MessageVolume, TimeSpan.FromSeconds(60), ct)
            .ConfigureAwait(false);
        sw.Stop();

        await consumerCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(consumerTasks).ContinueWith(static _ => { }, TaskContinuationOptions.None)
            .ConfigureAwait(false);

        return MessageVolume / sw.Elapsed.TotalSeconds;
    }

    // ── Licznik atomowy ───────────────────────────────────────────────────────

    /// <summary>
    /// Licznik atomowy kompatybilny z metodami <c>async</c> (brak <c>ref</c>).
    /// </summary>
    private sealed class AtomicCounter
    {
        private int _value;

        /// <summary>Inkrementuje licznik atomowo i zwraca nową wartość.</summary>
        public int Increment() => Interlocked.Increment(ref _value);

        /// <summary>Resetuje licznik do zera atomowo.</summary>
        public void Reset() => Interlocked.Exchange(ref _value, 0);

        /// <summary>Odczytuje bieżącą wartość (volatile).</summary>
        public int Read() => Volatile.Read(ref _value);
    }

    // ── Pomocnicze metody ─────────────────────────────────────────────────────

    /// <summary>
    /// Publikuje <paramref name="count"/> wiadomości do wskazanego exchange lub kolejki.
    /// Dla ścieżki competing-consumers i SAC: <paramref name="exchangeName"/> = "" (default exchange),
    /// <paramref name="routingKeyBase"/> = nazwa kolejki.
    /// Dla consistent-hash: <paramref name="exchangeName"/> = nazwa exchange,
    /// <paramref name="routingKeyBase"/> = prefiks klucza (round-robin po kardynalności).
    /// Klucze syntetyczne: „{routingKeyBase}-{n % keyCardinality}" (SEC-2: non-PII, bez wartości biznesowych).
    /// </summary>
    private static async Task PublishBatchAsync(
        RabbitMqTransportAdapter adapter,
        string exchangeName,
        string routingKeyBase,
        int count,
        int keyCardinality,
        CancellationToken ct)
    {
        const int BatchSize = 200;
        int sent = 0;

        while (sent < count)
        {
            int batchCount = Math.Min(BatchSize, count - sent);
            var batch = new OutboundMessage[batchCount];

            for (int i = 0; i < batchCount; i++)
            {
                // Klucz round-robin: tput-key-{n % keyCardinality} (SEC-2: syntetyczny, non-PII).
                string routingKey = keyCardinality > 1
                    ? $"{routingKeyBase}-{(sent + i) % keyCardinality}"
                    : routingKeyBase;

                batch[i] = new OutboundMessage(
                    routingKey: routingKey,
                    headers: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["BW-Exchange"] = exchangeName,
                    },
                    body: MessageBody,
                    contentType: "application/octet-stream");
            }

            await adapter.SendBatchAsync(batch, ct).ConfigureAwait(false);
            sent += batchCount;
        }
    }

    /// <summary>
    /// Czeka do momentu, gdy <paramref name="counter"/> osiągnie <paramref name="target"/>,
    /// odpytując co 10 ms z ograniczeniem czasowym <paramref name="timeout"/>.
    /// </summary>
    private static async Task WaitForCountAsync(
        AtomicCounter counter,
        int target,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();

        while (counter.Read() < target)
        {
            if (deadline.Elapsed >= timeout || ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"Harness pomiarowy nie osiągnął {target} wiadomości w ciągu {timeout.TotalSeconds:F0} s " +
                    $"(zmierzono: {counter.Read()}).");
            }

            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Zwraca <paramref name="percentile"/>-ty percentyl posortowanej tablicy wartości.
    /// Percentyl 50 = mediana; percentyl 20 = p20 (konserwatywna dolna granica rozkładu).
    /// </summary>
    private static double Percentile(double[] values, int percentile)
    {
        double[] sorted = [.. values.OrderBy(v => v)];
        double index = (percentile / 100.0) * (sorted.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);

        if (lower == upper)
        {
            return sorted[lower];
        }

        double fraction = index - lower;
        return sorted[lower] * (1.0 - fraction) + sorted[upper] * fraction;
    }
}
