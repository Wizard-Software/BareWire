using System.Reflection;

using AwesomeAssertions;

using NetArchTest.Rules;

using Xunit;

using BareWire.Abstractions;                        // IBus (Abstractions assembly anchor)
using BareWire.Abstractions.Configuration;          // ConsumerDefinition<>, IReceiveEndpointConfigurator
using BareWire.Transport.RabbitMQ.Configuration;    // ConsumerConfiguratorTopologyExtensions (transport seam)

namespace BareWire.ContractTests;

/// <summary>
/// Guards the transport-agnostic/transport-seam split for the Feature 19 consumer-definition surface.
/// <list type="bullet">
/// <item><description>I-2 — the AMQP topology helper (<c>DeclareTopology</c>) must live on the transport
/// seam (<c>BareWire.Transport.RabbitMQ</c>), never on the transport-agnostic <c>ConsumerDefinition&lt;T&gt;</c>
/// base type nor anywhere in zero-dep <c>BareWire.Abstractions</c>.</description></item>
/// <item><description>M-1 — auto-topology is NOT the default (manual topology remains opt-in). A structural
/// fitness guard against identity drift; a reversal of that default would remove or invert these opt-in seams.</description></item>
/// </list>
/// </summary>
public sealed class TopologySeamContractTests
{
    /// <summary>AMQP topology vocabulary that must never appear as a member of the transport-agnostic base type.</summary>
    private static readonly string[] AmqpTopologyVocabulary = ["Topology", "Exchange", "Binding"];

    // -------------------------------------------------------------------------
    // I-2: the AMQP topology helper is ABSENT from ConsumerDefinition<T> and from
    // Abstractions — it lives on the transport seam as an extension method in
    // BareWire.Transport.RabbitMQ.
    // -------------------------------------------------------------------------

    [Fact]
    public void AmqpTopologyHelper_IsAbsentFrom_ConsumerDefinitionAndAbstractions()
    {
        // (a) ConsumerDefinition<T> (the transport-agnostic base type) declares no AMQP topology member.
        var defMemberNames = typeof(ConsumerDefinition<>)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                        | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToArray();

        defMemberNames.Should().NotContain(
            n => AmqpTopologyVocabulary.Any(v => n.Contains(v, StringComparison.OrdinalIgnoreCase)),
            "the AMQP topology helper must live on the transport seam, not on the transport-agnostic ConsumerDefinition<T> base type");
        defMemberNames.Should().NotContain("DeclareTopology");

        // (b) No type in Abstractions declares a DeclareTopology / AMQP topology member.
        var abstractionsAssembly = typeof(IBus).Assembly;
        var offenders = abstractionsAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Equals("DeclareTopology", StringComparison.Ordinal))
            .Select(m => m.DeclaringType!.FullName)
            .ToArray();

        offenders.Should().BeEmpty("AMQP topology vocabulary must not leak into zero-dep Abstractions");

        // Belt-and-braces via NetArchTest: no type in Abstractions is named like the transport topology helper.
        var topologyTypesInAbstractions = Types.InAssembly(abstractionsAssembly)
            .That()
            .HaveNameEndingWith("TopologyExtensions")
            .GetTypes();

        topologyTypesInAbstractions.Should().BeEmpty(
            "AMQP topology helpers belong on the transport seam, not in Abstractions");

        // (c) Positive: the helper lives on the transport seam as a public static extension in Transport.RabbitMQ.
        typeof(ConsumerConfiguratorTopologyExtensions)
            .GetMethod("DeclareTopology")
            .Should().NotBeNull("the topology helper belongs on the transport seam (BareWire.Transport.RabbitMQ)");
    }

    // -------------------------------------------------------------------------
    // M-1: auto-topology is NOT the default (ADR-002 not reversed) — a structural
    // fitness guard against identity drift.
    //
    // Design note: the concrete IReceiveEndpointConfigurator implementations that
    // hold the `false` default are internal, and BareWire.ContractTests is not on
    // Core's InternalsVisibleTo list — so this is a public-surface structural guard
    // (it does not read the live default value). Reversing the manual-topology
    // default would remove or invert these opt-in seams.
    // -------------------------------------------------------------------------

    [Fact]
    public void AutoTopology_IsNotTheDefault_ManualTopologyRemainsOptIn()
    {
        // (a) The public opt-in switch that gates consume-topology creation still exists on the
        //     endpoint configurator interface as a read/write bool. Its removal or inversion would
        //     signal that auto-topology became the default (manual-topology default reversed).
        var gate = typeof(IReceiveEndpointConfigurator).GetProperty("ConfigureConsumeTopology");
        gate.Should().NotBeNull("the opt-in topology switch must remain on the public contract");
        gate!.PropertyType.Should().Be<bool>();
        gate.CanRead.Should().BeTrue();
        gate.CanWrite.Should().BeTrue("topology is opt-in — the user explicitly sets the flag");

        // (b) The AMQP topology declaration is an EXPLICIT opt-in helper the user must call — it lives
        //     as a public static method on the transport seam and is never auto-invoked. Its presence
        //     as an opt-in (rather than an auto-invocation) proves topology is opt-in, not automatic.
        typeof(ConsumerConfiguratorTopologyExtensions)
            .GetMethod("DeclareTopology")
            .Should().NotBeNull("the topology helper is an explicit opt-in — nothing invokes it automatically");
    }
}
