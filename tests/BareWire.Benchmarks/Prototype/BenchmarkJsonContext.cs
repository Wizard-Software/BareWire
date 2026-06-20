// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using System.Text.Json.Serialization;

namespace BareWire.Benchmarks.Prototype;

/// <summary>
/// STJ source-generation context for <see cref="BenchmarkOrder"/> used by the
/// <see cref="DynamicMethodVsSystemTextJsonBenchmarks"/> to provide a fair source-gen arm
/// in the three-way benchmark (STJ-reflection / STJ-source-gen / DynamicMethod).
/// Options mirror <c>BareWireJsonSerializerOptions.Default</c>:
///   - <see cref="JsonSerializerDefaults.Web"/> (case-insensitive reads, camelCase writes)
///   - <see cref="JsonKnownNamingPolicy.CamelCase"/>
///   - <see cref="JsonIgnoreCondition.WhenWritingNull"/>
///   - WriteIndented = false
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BenchmarkOrder))]
[JsonSerializable(typeof(BenchmarkOrderItem))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
