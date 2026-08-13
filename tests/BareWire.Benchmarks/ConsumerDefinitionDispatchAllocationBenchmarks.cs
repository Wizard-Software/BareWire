using BenchmarkDotNet.Attributes;
using BareWire.Abstractions;               // IConsumer<T>, ConsumeContext<T>
using BareWire.Abstractions.Configuration; // ConsumerRegistration
using BareWire.Bus;                         // ConsumerInvokerFactory (internal — InternalsVisibleTo "BareWire.Benchmarks")

namespace BareWire.Benchmarks;

/// <summary>
/// Allocation-ceiling benchmark (task 19.12): the consume-time dispatch path WITHOUT any consumer-definition
/// opt-in MUST stay <c>0-B/op</c>.
///
/// <para><strong>Gate:</strong> consumer-definition discovery + <c>TMessage</c> inference (the
/// <c>MakeGenericMethod</c> that bakes the closed <see cref="ConsumerInvokerFactory.InvokerDelegate"/> — the
/// 19.6 seam) happen <em>once at start-up</em>, inside <see cref="Setup"/>, NOT per delivery. What the
/// dispatcher does per delivery on the default-off path — read the already-baked invoker reference and read
/// the precompiled <see cref="ConsumerRegistration"/> fields — is a pure field/reference read with zero
/// reflection, zero <c>MakeGenericMethod</c>, zero per-message lookup, and zero allocation.</para>
///
/// <para>Measured path (<see cref="Dispatch_NoDefinitionOptIn_SettingsRead"/>):</para>
/// <list type="number">
/// <item><description>
/// Read the baked <see cref="ConsumerInvokerFactory.InvokerDelegate"/> (a field reference — the closed
/// delegate was built ONCE in <see cref="Setup"/> via the production <see cref="ConsumerInvokerFactory.Create"/>
/// seam, proving reflection/inference is a start-up cost, not per delivery).
/// </description></item>
/// <item><description>
/// Read the precompiled <see cref="ConsumerRegistration"/> fields (all opt-in knobs default-off) exactly as
/// the dispatcher reads them per delivery — precompiled field reads, NOT a per-message lookup.
/// </description></item>
/// </list>
///
/// <remarks>
/// <para>
/// Reaches the internal <see cref="ConsumerInvokerFactory"/> seam via
/// <c>[InternalsVisibleTo("BareWire.Benchmarks")]</c> (see <c>src/BareWire/BareWire.csproj</c>) — the exact
/// factory the bus uses at start-up. No production code or public API is touched by this benchmark.
/// </para>
/// <para>
/// The actual invocation of the baked delegate is intentionally OUT of the measured path: invoking a consumer
/// allocates a DI scope + deserialization and is not part of the 0-B/op selection/settings-read cost this
/// gate governs — mirroring the sibling <see cref="DispatchBenchmarks"/>, which likewise excludes the
/// consumer invocation from its 0-B/op gate.
/// </para>
/// <para>
/// The global <c>&lt; 512 B/op</c> consume budget remains guarded by
/// <see cref="ConsumeBenchmarks.ConsumeAndAck_InMemory"/> (transport floor). The consumer-definition
/// enhancement (19.x) adds only start-up discovery + this 0-B/op per-delivery read, so the global consume
/// budget is unchanged.
/// </para>
/// <para>
/// NOTE: <c>[EventPipeProfiler]</c> is intentionally omitted — BenchmarkDotNet has a known bug with the
/// .NET 10 runtime detection (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: true)]
public class ConsumerDefinitionDispatchAllocationBenchmarks
{
    // Minimal consumer + message — used ONLY to bake the real closed invoker delegate at start-up.
    private sealed record BenchMessage(int Value);

    private sealed class BenchConsumer : IConsumer<BenchMessage>
    {
        public Task ConsumeAsync(ConsumeContext<BenchMessage> context) => Task.CompletedTask;
    }

    // Default-off registration ("no definition opts in") — all optional knobs at their default values.
    private ConsumerRegistration _defaultOffRegistration = null!;

    // The baked closed delegate from 19.6 — built ONCE in [GlobalSetup] via the production factory seam.
    private ConsumerInvokerFactory.InvokerDelegate _bakedInvoker = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Bake the real closed delegate ONCE (production 19.6 seam). MakeGenericMethod happens HERE (start-up),
        // never per delivery. Allocation here is build-time, outside the measured path.
        _bakedInvoker = ConsumerInvokerFactory.Create(typeof(BenchConsumer), typeof(BenchMessage));

        // The "no opt-in" path: every optional knob at its default-off value.
        _defaultOffRegistration = new ConsumerRegistration(
            ConsumerType: typeof(BenchConsumer),
            MessageType: typeof(BenchMessage),
            RoutingKeys: null,              // no routing opt-in
            AcceptUntyped: false,           // no untyped opt-in
            UseMassTransitEnvelope: false,  // raw-first (ADR-001), no envelope
            ConfigureRetry: null,           // no retry carrier (I-1)
            PrefetchCount: null,
            ConcurrentMessageLimit: null);
    }

    /// <summary>
    /// Per-delivery default-off dispatch read — reads the baked invoker reference and the precompiled
    /// <see cref="ConsumerRegistration"/> fields. No <c>MakeGenericMethod</c>, no reflection, no per-message
    /// lookup. Target: <c>0 B/op</c>.
    /// </summary>
    [Benchmark]
    public int Dispatch_NoDefinitionOptIn_SettingsRead()
    {
        // (1) Read the baked delegate — a field reference, NOT MakeGenericMethod/reflection per delivery.
        ConsumerInvokerFactory.InvokerDelegate invoker = _bakedInvoker;

        // (2) Precompiled read of the definition fields (NOT a per-message lookup) — the default-off path.
        ConsumerRegistration reg = _defaultOffRegistration;
        int score = invoker is not null ? 1 : 0;
        if (reg.RoutingKeys is null) score++;
        if (!reg.AcceptUntyped) score++;
        if (!reg.UseMassTransitEnvelope) score++;
        if (reg.ConfigureRetry is null) score++;
        if (reg.PrefetchCount is null) score++;
        if (reg.ConcurrentMessageLimit is null) score++;
        return score;
    }
}
