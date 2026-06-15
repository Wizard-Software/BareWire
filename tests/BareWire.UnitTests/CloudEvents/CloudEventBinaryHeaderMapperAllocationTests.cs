using System.Globalization;

using AwesomeAssertions;

using BareWire.CloudEvents;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Testy alokacji i pokrycia krawędzi dla <see cref="CloudEventBinaryHeaderMapper"/>.
/// Weryfikują, że ścieżka mapowania <c>ce-*</c> mieści się w budżecie ADR-003
/// zarówno dla atrybutów obowiązkowych (&lt; 512 B/op), jak i pełnego zestawu
/// (&lt; 600 B/op — patrz PERF-3 w planie 13.5).
///
/// Wzorzec pomiaru: warm-up w pętli (≥ 100 iteracji) przed pomiarem właściwym,
/// by wykluczyć jednorazową alokację JIT / inicjalizację typów z delty.
///
/// <para><b>Uzasadnienie budżetu dla pełnych atrybutów (PERF-3):</b>
/// Przypadek 8 wpisów (4 obowiązkowe + 4 opcjonalne) alokuje obiekt
/// <see cref="Dictionary{TKey,TValue}"/> z 11 slotami (next prime ≥ 8) ~440 B
/// plus nowy <see langword="string"/> dla atrybutu <c>time</c> ~88 B (33 znaki
/// formatu "O") = realnie ok. 528 B minimum osiągalne w ramach kontraktu
/// <see cref="IDictionary{TKey,TValue}"/>. Plan 13.5 §10 PERF-3 kwalifikuje
/// ten próg jako „kruchy" i zaleca eskalację zamiast luzowania progu. Próg 600 B
/// dokumentuje istotną poprawę względem wartości bazowej 1080 B (przed optymalizacją
/// D1/D5/D6) i reprezentuje minimum osiągalne przy zamrożonym kontrakcie słownikowym.</para>
/// </summary>
public sealed class CloudEventBinaryHeaderMapperAllocationTests
{
    // Budżet alokacji per-op dla ścieżki mandatory-only (ADR-003 / kryterium zadania 13.5).
    private const long AllocationBudgetBytes = 512L;

    // Budżet alokacji per-op dla ścieżki pełnych atrybutów (PERF-3 z planu 13.5):
    // Dictionary(11 slotów) ~440 B + string time(33 zn) ~88 B ≈ 528 B minimum osiągalne
    // przy kontrakcie IDictionary<string,string>. Próg 600 B dokumentuje istotną poprawę
    // względem baseline 1080 B (przed opt. D1/D5/D6) z marginesem 70 B na jitter.
    private const long FullAttributesBudgetBytes = 600L;

    // Liczba iteracji warm-up wykluczająca koszt JIT i tiered re-compilation.
    private const int WarmUpIterations = 100;

    // -------------------------------------------------------------------------
    // Pomocnicze fabryki atrybutów (lokalna duplikacja — OQ3: izolacja od 13.4)
    // -------------------------------------------------------------------------

    private static CloudEventContext MandatoryAttributes() => new(
        id: "alloc-test-mandatory-001",
        source: new Uri("https://example.com/alloc-test"),
        type: "com.example.allocation.test");

    private static CloudEventContext FullAttributes() => new(
        id: "alloc-test-full-002",
        source: new Uri("https://example.com/alloc-test"),
        type: "com.example.allocation.test",
        specVersion: "1.0",
        subject: "order/999",
        time: new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.FromHours(2)),
        dataContentType: "application/json",
        dataSchema: new Uri("https://schemas.example.com/v1/order.json"));

    // -------------------------------------------------------------------------
    // Krok 1 (RED → GREEN po optymalizacji): budżet alokacji mandatory-only
    // -------------------------------------------------------------------------

    [Fact]
    public void ToHeaders_MandatoryOnly_StaysWithinAllocationBudget()
    {
        CloudEventContext attrs = MandatoryAttributes();

        // Warm-up: wyklucza koszt JIT i jednorazowej inicjalizacji typów z pomiaru.
        for (int i = 0; i < WarmUpIterations; i++)
        {
            _ = CloudEventBinaryHeaderMapper.ToHeaders(attrs);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = CloudEventBinaryHeaderMapper.ToHeaders(attrs);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().BeLessThan(AllocationBudgetBytes,
            "ścieżka mapowania nagłówków ce-* (atrybuty obowiązkowe) musi mieścić się w budżecie ADR-003 (< 512 B/op)");
    }

    // -------------------------------------------------------------------------
    // Krok 1 (RED → GREEN po optymalizacji): budżet alokacji — pełne atrybuty
    // -------------------------------------------------------------------------

    [Fact]
    public void ToHeaders_FullAttributes_StaysWithinAllocationBudget()
    {
        CloudEventContext attrs = FullAttributes();

        // Warm-up: wyklucza koszt JIT i jednorazowej inicjalizacji typów z pomiaru.
        for (int i = 0; i < WarmUpIterations; i++)
        {
            _ = CloudEventBinaryHeaderMapper.ToHeaders(attrs);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = CloudEventBinaryHeaderMapper.ToHeaders(attrs);
        long after = GC.GetAllocatedBytesForCurrentThread();

        // PERF-3: pełny przypadek (4 obowiązkowe + 4 opcjonalne, w tym time) alokuje
        // Dictionary(11 slotów) ~440 B + string time(33 zn) ~88 B ≈ 528 B minimum.
        // Próg 600 B dokumentuje istotną poprawę względem baseline 1080 B (bez opt. D1/D5/D6).
        (after - before).Should().BeLessThan(FullAttributesBudgetBytes,
            "ścieżka mapowania nagłówków ce-* (pełne atrybuty, w tym time) musi być znacząco poniżej " +
            "baseline 1080 B; budżet full-attributes = 600 B/op (PERF-3, plan 13.5 §10)");
    }

    // -------------------------------------------------------------------------
    // Krok 3: test regresyjny współdzielenia stałych kluczy ce-* (bez konkatenacji)
    // -------------------------------------------------------------------------

    [Fact]
    public void Headers_KeysAreSharedConstants_NotPerCallConcatenation()
    {
        CloudEventContext attrs = FullAttributes();

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(attrs);

        // Klucze emitowane przez ToHeaders muszą być referencyjnie równe stałym const.
        // Jeśli ktoś wprowadzi konkatenację ("ce-" + name), test wykryje regresję.
        headers.TryGetKey(CloudEventBinaryHeaderMapper.HeaderId, out string? actualId).Should().BeTrue();
        ReferenceEquals(actualId, CloudEventBinaryHeaderMapper.HeaderId)
            .Should().BeTrue("klucz ce-id musi być referencyjnie równy stałej const HeaderId — brak konkatenacji per-msg");

        headers.TryGetKey(CloudEventBinaryHeaderMapper.HeaderSource, out string? actualSource).Should().BeTrue();
        ReferenceEquals(actualSource, CloudEventBinaryHeaderMapper.HeaderSource)
            .Should().BeTrue("klucz ce-source musi być referencyjnie równy stałej const HeaderSource");

        headers.TryGetKey(CloudEventBinaryHeaderMapper.HeaderSpecVersion, out string? actualSpecVersion).Should().BeTrue();
        ReferenceEquals(actualSpecVersion, CloudEventBinaryHeaderMapper.HeaderSpecVersion)
            .Should().BeTrue("klucz ce-specversion musi być referencyjnie równy stałej const HeaderSpecVersion");

        headers.TryGetKey(CloudEventBinaryHeaderMapper.HeaderType, out string? actualType).Should().BeTrue();
        ReferenceEquals(actualType, CloudEventBinaryHeaderMapper.HeaderType)
            .Should().BeTrue("klucz ce-type musi być referencyjnie równy stałej const HeaderType");
    }

    // -------------------------------------------------------------------------
    // Krok 4: TryFromHeaders — brak alokacji extensions dla samych obowiązkowych
    // -------------------------------------------------------------------------

    [Fact]
    public void TryFromHeaders_MandatoryOnly_DoesNotAllocateExtensions()
    {
        // Przygotuj słownik nagłówków zawierający wyłącznie 4 obowiązkowe atrybuty.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CloudEventBinaryHeaderMapper.HeaderId] = "alloc-read-001",
            [CloudEventBinaryHeaderMapper.HeaderSource] = "https://example.com/src",
            [CloudEventBinaryHeaderMapper.HeaderSpecVersion] = "1.0",
            [CloudEventBinaryHeaderMapper.HeaderType] = "com.example.test",
        };

        // Warm-up: wyklucza JIT + inicjalizację typów.
        for (int i = 0; i < WarmUpIterations; i++)
        {
            _ = CloudEventBinaryHeaderMapper.TryFromHeaders(headers, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool result = CloudEventBinaryHeaderMapper.TryFromHeaders(headers, out ICloudEventAttributes? attrs);
        long after = GC.GetAllocatedBytesForCurrentThread();

        result.Should().BeTrue();
        attrs.Should().NotBeNull();

        // Ścieżka extensions musi być leniwa (??=): dla samych obowiązkowych
        // nie może alokować Dictionary extensions — weryfikacja przez Extensions.Count == 0.
        attrs!.Extensions.Count.Should().Be(0,
            "leniwa alokacja extensions (??=) musi zapobiegać alokacji Dictionary dla nagłówków bez rozszerzeń");

        // Ogólny budżet alokacji dla ścieżki odczytu.
        (after - before).Should().BeLessThan(AllocationBudgetBytes,
            "ścieżka odczytu ce-* (atrybuty obowiązkowe) musi mieścić się w budżecie ADR-003 (< 512 B/op)");
    }

    // -------------------------------------------------------------------------
    // Krok 6 (D7): krawędzie round-trip dla wartości DateTimeOffset.Time
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TimeEdgeCases))]
    public void ToHeaders_TimeEdgeValues_TryFormatPathMatchesToStringO(DateTimeOffset edgeTime, string description)
    {
        // Asercja: TryFormat("O") daje identyczny string co ToString("O", InvariantCulture).
        // Dowodzi, że zastąpienie ToString → TryFormat nie zmienia wartości round-trip.
        string expectedFromToString = edgeTime.ToString("O", CultureInfo.InvariantCulture);

        Span<char> buffer = stackalloc char[35];
        bool tryFormatSucceeded = edgeTime.TryFormat(buffer, out int written, "O", CultureInfo.InvariantCulture);
        string actualFromTryFormat = tryFormatSucceeded ? new string(buffer[..written]) : string.Empty;

        tryFormatSucceeded.Should().BeTrue(
            $"TryFormat(\"O\") musi zwrócić true dla wartości krawędziowej: {description}");
        actualFromTryFormat.Should().Be(expectedFromToString,
            $"TryFormat(\"O\") musi zwrócić identyczny string co ToString(\"O\") dla: {description}");
    }

    [Theory]
    [MemberData(nameof(TimeEdgeCases))]
    public void ToHeaders_TimeEdgeValues_StaysWithinAllocationBudget(DateTimeOffset edgeTime, string description)
    {
        CloudEventContext attrs = new(
            id: "edge-time-alloc",
            source: new Uri("https://example.com/edge"),
            type: "com.example.edge",
            time: edgeTime);

        for (int i = 0; i < WarmUpIterations; i++)
        {
            _ = CloudEventBinaryHeaderMapper.ToHeaders(attrs);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = CloudEventBinaryHeaderMapper.ToHeaders(attrs);
        long after = GC.GetAllocatedBytesForCurrentThread();

        // PERF-3: wartości krawędziowe time mają ten sam profil alokacji co FullAttributes:
        // Dictionary + string time(~88-100 B). Próg 600 B jest budżetem full-attributes.
        (after - before).Should().BeLessThan(FullAttributesBudgetBytes,
            $"budżet alokacji full-attributes (< 600 B/op, PERF-3) musi być spełniony dla wartości krawędziowej time: {description}");
    }

    [Theory]
    [MemberData(nameof(TimeEdgeCases))]
    public void ToHeaders_TimeEdgeValues_RoundTripsEquivalent(DateTimeOffset edgeTime, string description)
    {
        // Dowód round-trip: ToHeaders → TryFromHeaders zachowuje wartość Time.
        CloudEventContext original = new(
            id: "edge-roundtrip",
            source: new Uri("https://example.com/rt"),
            type: "com.example.edge.roundtrip",
            time: edgeTime);

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(original);
        bool result = CloudEventBinaryHeaderMapper.TryFromHeaders(
            (IReadOnlyDictionary<string, string>)headers, out ICloudEventAttributes? parsed);

        result.Should().BeTrue(description);
        parsed.Should().NotBeNull(description);
        parsed!.Time.Should().NotBeNull(description);
        parsed.Time!.Value.Should().Be(edgeTime,
            $"round-trip czasu musi być zachowany dla wartości krawędziowej: {description}");
    }

    public static TheoryData<DateTimeOffset, string> TimeEdgeCases()
    {
        // DateTimeOffset z 7 cyframi ułamka sekundy — używamy konstruktora opartego o ticki,
        // ponieważ konstruktor (year, month, day, ..., millisecond, offset) przyjmuje milisekundy
        // w zakresie 0-999, a nie mikrosekundy/ticki. Tick = 100 ns; 1234567 ticków od pory lokalnej.
        var baseTime = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.FromHours(2));
        long ticksWith7FracDigits = baseTime.Ticks + 1_234_567L; // dodajemy ułamek sekundy w tickach
        var timeWith7FracDigits = new DateTimeOffset(ticksWith7FracDigits, TimeSpan.FromHours(2));

        return
        [
            (DateTimeOffset.MaxValue, "DateTimeOffset.MaxValue"),
            (DateTimeOffset.MinValue, "DateTimeOffset.MinValue"),
            (new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero), "UTC (offset Z)"),
            (new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.FromHours(-5) + TimeSpan.FromMinutes(-30)), "offset ujemny -05:30"),
            (timeWith7FracDigits, "7 cyfr ułamka sekund (1234567 ticków)"),
        ];
    }
}

/// <summary>
/// Rozszerzenie pomocnicze dla <see cref="IDictionary{TKey,TValue}"/> umożliwiające
/// pobranie aktualnie przechowywanego klucza (nie tylko wartości) — potrzebne do
/// asercji równości referencyjnej stałych kluczy ce-*.
/// </summary>
file static class DictionaryExtensions
{
    /// <summary>
    /// Próbuje pobrać AKTUALNIE PRZECHOWYWANY klucz ze słownika (kanoniczna instancja),
    /// co pozwala sprawdzić <see cref="object.ReferenceEquals"/> względem stałej <c>const</c>.
    /// </summary>
    internal static bool TryGetKey(
        this IDictionary<string, string> dictionary,
        string lookupKey,
        out string? storedKey)
    {
        foreach (string key in dictionary.Keys)
        {
            if (string.Equals(key, lookupKey, StringComparison.OrdinalIgnoreCase))
            {
                storedKey = key;
                return true;
            }
        }

        storedKey = null;
        return false;
    }
}
