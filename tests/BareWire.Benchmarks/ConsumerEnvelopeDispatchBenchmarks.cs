using System.Buffers;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization;
using BenchmarkDotNet.Attributes;

namespace BareWire.Benchmarks;

/// <summary>
/// Allocation-ceiling benchmark for the per-consumer MassTransit-envelope deserializer selection on the
/// consume-time dispatch path (task 18.5, ADR-031 D4 precedence).
///
/// <para><strong>Gate (Feature 18 — secure-by-default opt-in):</strong> when NO consumer on an endpoint
/// opts into <c>UseMassTransitEnvelope()</c> (the default for ~all deployments), the per-delivery
/// deserializer-selection arithmetic MUST be <c>0-B/op</c> — i.e. the dispatch path degrades to the
/// exact pre-18.5 behaviour. The opt-in must not tax the path that does not use it.</para>
///
/// <para>This mirrors, one-to-one, the production selection seam
/// <c>ReceiveEndpointRunner.ResolverFor(int)</c>:</para>
/// <code>
/// [MethodImpl(MethodImplOptions.AggressiveInlining)]
/// private IDeserializerResolver ResolverFor(int i) =>
///     _hasAnyMtEnvelope &amp;&amp; _consumerUseMtEnvelope[i] ? _mtResolver! : _deserializerResolver;
/// </code>
/// <para>When <c>_hasAnyMtEnvelope == false</c>, the <c>&amp;&amp;</c> short-circuits and returns the
/// reference-identical <c>_deserializerResolver</c> — no allocation, no array index. That is the
/// "degradation to today's path" the gate governs.</para>
///
/// <para>Two measured paths, each isolating only the selection arithmetic the production dispatcher runs
/// per delivery (the consumer invocation itself — scope creation + payload deserialization — is out of
/// scope: it allocates by definition and is not part of the selection cost the gate governs, exactly as
/// noted in <see cref="DispatchBenchmarks"/>):</para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Select_NoOptIn"/> — the no-opt-in path (<c>_hasAnyMtEnvelope == false</c>): the production
/// short-circuit returning <c>_deserializerResolver</c>. Target: <c>0 B/op</c> (the gate).
/// </description></item>
/// <item><description>
/// <see cref="Select_AllOptIn"/> — contrast path (every consumer opts in): returns <c>_mtResolver</c>.
/// Documents that the selection arithmetic itself is always allocation-free; the cost difference of
/// opting in lives downstream in deserialization, not in selection. Expected: <c>0 B/op</c>.
/// </description></item>
/// </list>
///
/// <remarks>
/// <para>
/// Reaches the internal <see cref="SingleDeserializerResolver"/> seam — the exact resolver type the
/// runner builds for <c>_mtResolver</c> and wraps per-endpoint overrides with — via
/// <c>[InternalsVisibleTo("BareWire.Benchmarks")]</c> (see <c>src/BareWire/BareWire.csproj</c>). No
/// production code or public API is touched by this benchmark.
/// </para>
/// <para>
/// The scenario flags are held as instance fields (not <c>const</c>) so the JIT cannot fold the branch
/// away — the measured ternary is a genuine runtime branch, mirroring the production <c>readonly</c>
/// fields. The benchmark return value is consumed by BenchmarkDotNet, so the selection is not eliminated
/// as dead code.
/// </para>
/// <para>
/// NOTE: <c>[EventPipeProfiler]</c> is intentionally omitted — BenchmarkDotNet has a known bug with
/// .NET 10 runtime detection (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: true)]
public class ConsumerEnvelopeDispatchBenchmarks
{
    // Representative endpoint fan-in: a handful of consumers bound to one endpoint. The selection runs
    // once per consumer candidate on the typed dispatch path (ResolverFor(i) at each invoker call site).
    private const int ConsumerCount = 3;

    // Same resolver types as ReceiveEndpointRunner: _deserializerResolver (per-endpoint / global) and
    // _mtResolver (the MT-envelope SingleDeserializerResolver built once when any consumer opts in).
    private IDeserializerResolver _deserializerResolver = null!;
    private SingleDeserializerResolver _mtResolver = null!;

    // 1:1 with consumers, mirroring ReceiveEndpointRunner._consumerUseMtEnvelope. Held all-true so the
    // all-opt-in path exercises the second &&-operand; on the no-opt-in path the array is never read
    // because _hasAnyMtEnvelopeOff short-circuits the && first (exactly as in production).
    private bool[] _consumerUseMtEnvelope = null!;

    // Mirrors the runner's _hasAnyMtEnvelope guard for the two scenarios.
    private bool _hasAnyMtEnvelopeOff;
    private bool _hasAnyMtEnvelopeOn;

    [GlobalSetup]
    public void Setup()
    {
        // Build-time allocation is permitted (outside the measured path).
        IMessageDeserializer noop = new NoopDeserializer();
        _deserializerResolver = new SingleDeserializerResolver(noop);
        _mtResolver = new SingleDeserializerResolver(noop);

        _consumerUseMtEnvelope = new bool[ConsumerCount];
        Array.Fill(_consumerUseMtEnvelope, true);

        _hasAnyMtEnvelopeOff = false;
        _hasAnyMtEnvelopeOn = true;
    }

    /// <summary>
    /// No-opt-in selection path (<c>_hasAnyMtEnvelope == false</c>) — mirrors
    /// <c>ReceiveEndpointRunner.ResolverFor(int)</c> when no consumer opted in: the <c>&amp;&amp;</c>
    /// short-circuits and returns the reference-identical <c>_deserializerResolver</c>. Target:
    /// <c>0 B/op</c> (the Feature 18 gate — degradation to the pre-18.5 path).
    /// </summary>
    [Benchmark]
    public IDeserializerResolver Select_NoOptIn()
    {
        IDeserializerResolver last = _deserializerResolver;
        for (int i = 0; i < ConsumerCount; i++)
        {
            last = _hasAnyMtEnvelopeOff && _consumerUseMtEnvelope[i] ? _mtResolver : _deserializerResolver;
        }

        return last;
    }

    /// <summary>
    /// All-opt-in contrast path (<c>_hasAnyMtEnvelope == true</c>, every consumer marked) — returns
    /// <c>_mtResolver</c>. Documents that the selection arithmetic is allocation-free regardless of the
    /// opt-in flag; the opt-in cost lives downstream in deserialization, not in selection. Expected:
    /// <c>0 B/op</c>.
    /// </summary>
    [Benchmark]
    public IDeserializerResolver Select_AllOptIn()
    {
        IDeserializerResolver last = _deserializerResolver;
        for (int i = 0; i < ConsumerCount; i++)
        {
            last = _hasAnyMtEnvelopeOn && _consumerUseMtEnvelope[i] ? _mtResolver : _deserializerResolver;
        }

        return last;
    }

    // Stub deserializer: never invoked on the selection path (selection returns the resolver reference,
    // not a deserialized value), so its behaviour is immaterial to the measured allocation.
    private sealed class NoopDeserializer : IMessageDeserializer
    {
        public string ContentType => "application/x-benchmark-noop";

        public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class => null;
    }
}
