// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// R8.15 — default-OFF byte-for-byte regression contract (ADR-026 §Egzekwowanie "Domyślnie OFF = bit-identyczna ścieżka").
///
/// Verifies that a <see cref="BareWire.Abstractions.EndpointBinding"/> configured WITHOUT
/// <c>OrderedBy</c>/<c>OrderedByHeader</c> carries a <see langword="null"/>
/// <see cref="EndpointBinding.Ordering"/> — meaning the ordered dispatch path is completely dead
/// and behaviour is bit-identical to the pre-R8 sequential pump.
///
/// These tests are the explicit REGRESSION CONTRACT for the default-OFF invariant: any change that
/// accidentally enables ordering without an explicit <c>OrderedBy</c> call will cause them to fail.
/// </summary>
public sealed class OrderingDefaultOffRegressionTests
{
    /// <summary>
    /// Regression contract: a RabbitMQ endpoint configured WITHOUT <c>OrderedBy</c> produces a binding
    /// with <see cref="EndpointBinding.Ordering"/> == <see langword="null"/>.
    ///
    /// <para>ADR-026 §Egzekwowanie — "Domyślnie OFF = bit-identyczna ścieżka": the ordered dispatch path
    /// is engaged ONLY when <c>Ordering != null</c>; without <c>OrderedBy</c> the path is dead and the
    /// behaviour is bit-identical to the pre-R8 sequential pump.</para>
    /// </summary>
    [Fact]
    public void AddBareWireRabbitMq_NoOrderedBy_LeavesBindingOrderingNull_RegressionContract()
    {
        // Arrange — configure a RabbitMQ endpoint with no OrderedBy call at all.
        var services = new ServiceCollection();
        services.AddBareWireRabbitMq(cfg =>
        {
            cfg.Host("amqp://guest:guest@localhost:5672/");
            cfg.ReceiveEndpoint("plain-queue", e =>
            {
                e.ConcurrentMessageLimit = 4;
                // NO OrderedBy — the ordered path must remain DEAD.
            });
        });

        // Act
        IReadOnlyList<EndpointBinding> bindings =
            services.BuildServiceProvider().GetRequiredService<IReadOnlyList<EndpointBinding>>();

        // Assert — Ordering must be null: default-OFF = ordered path dead = bit-identical to pre-R8.
        EndpointBinding binding = bindings.Single(b => b.EndpointName == "plain-queue");
        binding.Ordering.Should().BeNull(
            "no OrderedBy was called — per-key ordering is OFF by default (ADR-026 §Egzekwowanie: " +
            "default-OFF = bit-identical path; Ordering != null would engage the ordered dispatch stage " +
            "without the caller requesting it — regression of R8.15 default-OFF contract)");
    }

    /// <summary>
    /// Regression contract: setting <c>ConcurrentMessageLimit</c> alone (without <c>OrderedBy</c>)
    /// must NOT engage per-key ordering — <c>ConcurrentMessageLimit</c> is load-bearing ONLY when
    /// <c>OrderedBy</c> is present.
    ///
    /// <para>ADR-026 §Egzekwowanie — a high <c>ConcurrentMessageLimit</c> value must not accidentally
    /// trigger the ordered dispatch path. Only an explicit <c>OrderedBy</c>/<c>OrderedByHeader</c>
    /// call activates ordering.</para>
    /// </summary>
    [Fact]
    public void AddBareWireRabbitMq_NoOrderedBy_HighConcurrentMessageLimit_StillNoOrdering()
    {
        // Arrange — high ConcurrentMessageLimit but still no OrderedBy.
        var services = new ServiceCollection();
        services.AddBareWireRabbitMq(cfg =>
        {
            cfg.Host("amqp://guest:guest@localhost:5672/");
            cfg.ReceiveEndpoint("high-concurrency-queue", e =>
            {
                e.ConcurrentMessageLimit = 8;
                // NO OrderedBy — ConcurrentMessageLimit alone must NOT engage ordering.
            });
        });

        // Act
        IReadOnlyList<EndpointBinding> bindings =
            services.BuildServiceProvider().GetRequiredService<IReadOnlyList<EndpointBinding>>();

        // Assert — Ordering must still be null even with a high ConcurrentMessageLimit.
        EndpointBinding binding = bindings.Single(b => b.EndpointName == "high-concurrency-queue");
        binding.Ordering.Should().BeNull(
            "ConcurrentMessageLimit=8 alone must NOT engage per-key ordering " +
            "(ADR-026 §Egzekwowanie: ConcurrentMessageLimit is load-bearing ONLY when OrderedBy is present; " +
            "a non-null Ordering here would mean the ordered path engages without an explicit OrderedBy call — " +
            "regression of R8.15 default-OFF contract)");
    }
}
