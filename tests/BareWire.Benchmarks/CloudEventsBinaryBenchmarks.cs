using BareWire.CloudEvents;

using BenchmarkDotNet.Attributes;

namespace BareWire.Benchmarks;

/// <summary>
/// Benchmark zero-alokacyjnej ścieżki mapowania binary-mode CloudEvents (ADR-003, PERF-1).
/// Mierzy alokację per-op <see cref="CloudEventBinaryHeaderMapper.ToHeaders"/> dla dwóch
/// scenariuszy: wyłącznie atrybuty obowiązkowe (baseline) i pełny zestaw (z <c>time</c>).
///
/// <para>Kolumna <c>Allocated</c> (<see cref="MemoryDiagnoserAttribute"/>) stanowi artefakt
/// dowodowy dla kryterium PERF-1 (&lt; 512 B/op mandatory, &lt; 600 B/op full).
/// Benchmark uruchamiany jest ręcznie / diagnostycznie — CI nie bramkuje merge'a na
/// podstawie tego benchmarku (D8 z planu 13.5; jedyną automatyczną bramką jest test
/// jednostkowy alokacji w <c>CloudEventBinaryHeaderMapperAllocationTests</c>).</para>
/// </summary>
[MemoryDiagnoser(displayGenColumns: true)]
public class CloudEventsBinaryBenchmarks
{
    private CloudEventContext _mandatory = null!;
    private CloudEventContext _full = null!;

    /// <summary>
    /// Inicjalizuje obiekty atrybutów używane przez wszystkie warianty benchmarku.
    /// Wywoływana raz przed całym zestawem pomiarów.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _mandatory = new CloudEventContext(
            id: "bench-mandatory-001",
            source: new Uri("https://example.com/bench"),
            type: "com.example.benchmark.mandatory");

        _full = new CloudEventContext(
            id: "bench-full-002",
            source: new Uri("https://example.com/bench"),
            type: "com.example.benchmark.full",
            specVersion: "1.0",
            subject: "order/12345",
            time: new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.FromHours(2)),
            dataContentType: "application/json",
            dataSchema: new Uri("https://schemas.example.com/v1/order.json"));
    }

    /// <summary>
    /// Mapowanie wyłącznie 4 obowiązkowych atrybutów CloudEvents.
    /// Ścieżka hot-path bez atrybutu <c>time</c> (brak alokacji nowego stringa).
    /// Budżet: &lt; 512 B/op (ADR-003 / PERF-1).
    /// </summary>
    [Benchmark(Baseline = true)]
    public IDictionary<string, string> ToHeaders_Mandatory() =>
        CloudEventBinaryHeaderMapper.ToHeaders(_mandatory);

    /// <summary>
    /// Mapowanie pełnego zestawu atrybutów CloudEvents (4 obowiązkowe + 4 opcjonalne).
    /// Jedyna nieunikniona alokacja ponad słownik to <c>new string</c> dla atrybutu <c>time</c>
    /// (format „O", 33 znaki, ~88 B) — D1/D5: przez <see cref="Span{T}"/> stackalloc,
    /// bez pośredniego bufora wewnętrznego <c>ToString("O")</c>.
    /// Budżet: &lt; 600 B/op (PERF-3 z planu 13.5 §10; minimum osiągalne ≈ 528 B).
    /// </summary>
    [Benchmark]
    public IDictionary<string, string> ToHeaders_Full() =>
        CloudEventBinaryHeaderMapper.ToHeaders(_full);
}
