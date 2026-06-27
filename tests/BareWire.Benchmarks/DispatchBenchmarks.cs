using BenchmarkDotNet.Attributes;
using BareWire.Routing;

namespace BareWire.Benchmarks;

/// <summary>
/// Allocation-ceiling benchmark for the consume-time routing-key dispatch selection path (ADR-030, 17.12).
///
/// <para><strong>Gate (ADR-030 §Wydajność):</strong> the per-delivery dispatch selection arithmetic
/// MUST be <c>0-B/op</c> — prebuilt pattern segments + integer specificity score + indexed <c>for</c> +
/// single-pass over <see cref="System.ReadOnlySpan{T}"/>; no <c>string.Split</c>, no <c>Any(lambda)</c>,
/// no per-delivery <c>List</c>.</para>
///
/// <para>Two measured paths, each isolating the selection arithmetic that the production dispatcher runs
/// per delivery (the consumer invocation itself is out of scope — it allocates scope/deserialization and
/// is not part of the selection cost the gate governs):</para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Dispatch_CatchAll"/> — mirrors <c>ReceiveEndpointRunner.SelectTypedLegacyAsync</c> header
/// fast-path (the <c>_hasAnyRoutingKeys == false</c> guard early-break): an indexed type-name compare
/// loop, break-on-first. Target: <c>0 B/op</c>.
/// </description></item>
/// <item><description>
/// <see cref="Dispatch_Matched"/> — mirrors <c>ReceiveEndpointRunner.SelectTypedWithPatternsAsync</c>
/// layer 1 (ADR-030 D4): pairwise most-specific-wins scan over precompiled
/// <see cref="CompiledTopicPattern"/> using <see cref="ITopicMatcher.IsMatch"/> +
/// <see cref="ITopicMatcher.CompareSpecificity"/>. Target: <c>0 B/op</c>.
/// </description></item>
/// </list>
///
/// <remarks>
/// <para>
/// Reaches the internal matcher seam (<see cref="TopicPatternMatcher"/>) via
/// <c>[InternalsVisibleTo("BareWire.Benchmarks")]</c> (see <c>src/BareWire/BareWire.csproj</c>) — the
/// exact same primitives the dispatcher uses per delivery. No production code or public API is touched
/// by this benchmark.
/// </para>
/// <para>
/// The global <c>&lt; 512 B/op</c> consume budget remains guarded by
/// <see cref="ConsumeBenchmarks.ConsumeAndAck_InMemory"/> (transport floor, unchanged by ADR-030 —
/// the enhancement adds only this 0-B/op selection arithmetic, so the global budget is preserved).
/// </para>
/// <para>
/// Patterns are kept short (≤ 3 segments) so <see cref="TopicPatternMatcher.IsMatch"/> exercises its
/// documented <c>stackalloc</c> DP path (the norm — AMQP routing keys are bounded to 255 bytes), keeping
/// the measured path free of <see cref="System.Buffers.ArrayPool{T}"/> rentals.
/// </para>
/// <para>
/// NOTE: <c>[EventPipeProfiler]</c> is intentionally omitted — BenchmarkDotNet has a known bug with
/// .NET 10 runtime detection (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: true)]
public class DispatchBenchmarks
{
    // Same matcher type as ReceiveEndpointRunner.s_topicMatcher — the production per-delivery seam.
    private readonly TopicPatternMatcher _matcher = new();

    // Precompiled in [GlobalSetup] — allocation here is permitted (Build-time, outside the measured path).
    private CompiledTopicPattern[] _patterns = null!;
    private string[] _typeNames = null!;

    // Held as string fields; converted to ReadOnlySpan<char> per invocation via AsSpan() (zero-alloc,
    // matching production's (routingKeyValue ?? string.Empty).AsSpan()).
    private string _routingKey = null!;
    private string _targetType = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Representative short patterns -> IsMatch stackalloc path (documented norm; SegmentCount + 1 <= 256).
        _patterns =
        [
            _matcher.Compile("lazy.#"),
            _matcher.Compile("*.orange.*"),
            _matcher.Compile("quick.brown.fox"),
        ];

        _typeNames = ["OrderEvent", "PaymentEvent", "ShipmentEvent"];

        // Routing key matches the exact (most-specific) pattern; target type is last -> full loop traversal.
        _routingKey = "quick.brown.fox";
        _targetType = "ShipmentEvent";
    }

    /// <summary>
    /// Catch-all selection path — mirrors <c>ReceiveEndpointRunner.SelectTypedLegacyAsync</c> header
    /// fast-path (guard <c>_hasAnyRoutingKeys == false</c>): indexed type-name compare, break-on-first.
    /// Target: <c>0 B/op</c>.
    /// </summary>
    [Benchmark]
    public int Dispatch_CatchAll()
    {
        ReadOnlySpan<char> target = _targetType.AsSpan();
        for (int i = 0; i < _typeNames.Length; i++)
        {
            // Production uses string.Equals(string, string, Ordinal) on already-allocated names; the span
            // overload here is likewise 0-B/op and avoids materializing intermediates.
            if (MemoryExtensions.Equals(_typeNames[i].AsSpan(), target, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Matched selection path — mirrors <c>ReceiveEndpointRunner.SelectTypedWithPatternsAsync</c> layer 1
    /// (ADR-030 D4): pairwise most-specific-wins scan using <see cref="ITopicMatcher.IsMatch"/> +
    /// <see cref="ITopicMatcher.CompareSpecificity"/> over precompiled patterns. Target: <c>0 B/op</c>.
    /// </summary>
    [Benchmark]
    public int Dispatch_Matched()
    {
        ReadOnlySpan<char> routingKey = _routingKey.AsSpan();
        int bestIdx = -1;
        CompiledTopicPattern best = default;

        for (int p = 0; p < _patterns.Length; p++)
        {
            if (!_matcher.IsMatch(in _patterns[p], routingKey))
            {
                continue;
            }

            if (bestIdx == -1 || _matcher.CompareSpecificity(in _patterns[p], in best) > 0)
            {
                bestIdx = p;
                best = _patterns[p];
            }
        }

        return bestIdx;
    }
}
