// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using BareWire.Benchmarks.Prototype;
using BareWire.Buffers;
using BareWire.Serialization.Json;

using BenchmarkDotNet.Attributes;

namespace BareWire.Benchmarks;

/// <summary>
/// Three-way benchmark: DynamicMethod prototype vs STJ-reflection vs STJ-source-gen.
/// Measures allocation (B/op) and throughput for the serialize path only.
/// Deserialize is NOT benchmarked here — the spike verdict is scoped to serialization (D-6a).
/// </summary>
/// <remarks>
/// Run: dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*DynamicMethodVs*'
/// </remarks>
[MemoryDiagnoser(displayGenColumns: true)]
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet manages object lifetime via [GlobalCleanup].")]
public class DynamicMethodVsSystemTextJsonBenchmarks
{
    [Params(100, 1_000, 10_000, 100_000)]
    public int PayloadSizeBytes { get; set; }

    private static readonly JsonWriterOptions s_writerOptions = new() { SkipValidation = true };

    // Thread-static pooled writer for the source-gen arm, mirroring the [ThreadStatic]
    // pooling in SystemTextJsonSerializer and the DynamicMethod prototype (D-5 / PERF-1).
    // Without this, the source-gen arm would pay a ~448 B Utf8JsonWriter allocation per op
    // that the other two arms avoid, skewing the three-way B/op comparison.
    [ThreadStatic]
    private static Utf8JsonWriter? t_sourceGenWriter;

    private SystemTextJsonSerializer _stjReflection = null!;
    private PooledBufferWriter _writer = null!;
    private BenchmarkOrder _payload = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stjReflection = new SystemTextJsonSerializer();
        _writer = new PooledBufferWriter(initialCapacity: PayloadSizeBytes * 4);
        _payload = GeneratePayload(PayloadSizeBytes);

        // Pre-warm the DynamicMethod delegate cache (PERF-2):
        // the first call per type absorbs the one-time emit cost so that measured
        // iterations reflect pure dispatch + property-write overhead only.
        using var warmupBuf = new PooledBufferWriter(initialCapacity: 256);
        DynamicMethodJsonSerializerPrototype.Serialize(_payload, warmupBuf);
    }

    /// <summary>
    /// Baseline: <c>SystemTextJsonSerializer</c> with runtime reflection.
    /// Uses thread-static <c>Utf8JsonWriter</c> pooling (same as prototype).
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Serialize_StjReflection()
    {
        _writer.Reset();
        _stjReflection.Serialize(_payload, _writer);
        return _writer.WrittenCount;
    }

    /// <summary>
    /// STJ source-generation arm — the fair competitor.
    /// No runtime reflection; type metadata generated at compile time.
    /// Uses thread-static <c>Utf8JsonWriter</c> pooling (same as the other two arms, D-5)
    /// so the B/op comparison measures serialize overhead, not writer allocation.
    /// </summary>
    [Benchmark]
    public int Serialize_StjSourceGen()
    {
        _writer.Reset();
        Utf8JsonWriter writer = t_sourceGenWriter ??= new Utf8JsonWriter(Stream.Null, s_writerOptions);
        writer.Reset(_writer);
        try
        {
            JsonSerializer.Serialize(writer, _payload, BenchmarkJsonContext.Default.BenchmarkOrder);
            writer.Flush();
        }
        finally
        {
            writer.Reset(Stream.Null);
        }

        return _writer.WrittenCount;
    }

    /// <summary>
    /// DynamicMethod prototype — per-type emitted delegate, cached after first call.
    /// Thread-static <c>Utf8JsonWriter</c> pooling mirrors the STJ-reflection baseline.
    /// </summary>
    [Benchmark]
    public int Serialize_DynamicMethod()
    {
        _writer.Reset();
        DynamicMethodJsonSerializerPrototype.Serialize(_payload, _writer);
        return _writer.WrittenCount;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _writer.Dispose();
    }

    // -------------------------------------------------------------------------
    // Payload generation — mirrors SerializationBenchmarks.GeneratePayload
    // -------------------------------------------------------------------------

    private static BenchmarkOrder GeneratePayload(int targetSize)
    {
        const int bytesPerItem = 80;
        int estimatedItems = Math.Max(1, (targetSize - 60) / bytesPerItem);

        BenchmarkOrder order = BuildOrder(estimatedItems);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            int actualSize = MeasureSerializedSize(order);
            double ratio = (double)actualSize / targetSize;

            if (ratio >= 0.9 && ratio <= 1.1)
                break;

            estimatedItems = Math.Max(1, (int)(estimatedItems * ((double)targetSize / actualSize)));
            order = BuildOrder(estimatedItems);
        }

        return order;
    }

    private static BenchmarkOrder BuildOrder(int itemCount)
    {
        var items = new List<BenchmarkOrderItem>(itemCount);

        for (int i = 0; i < itemCount; i++)
        {
            items.Add(new BenchmarkOrderItem(
                ProductId: $"PROD-{i:D5}",
                Name: $"Product Name {i}",
                Quantity: (i % 10) + 1,
                Price: 9.99m + i));
        }

        return new BenchmarkOrder(
            OrderId: "ORD-20260318-001",
            Amount: items.Sum(x => x.Price * x.Quantity),
            Currency: "PLN",
            Items: items);
    }

    private static int MeasureSerializedSize(BenchmarkOrder order)
    {
        using var tempWriter = new PooledBufferWriter(initialCapacity: 4096);
        using var jsonWriter = new Utf8JsonWriter(tempWriter);
        JsonSerializer.Serialize(jsonWriter, order);
        jsonWriter.Flush();
        return tempWriter.WrittenCount;
    }
}
