using System.Reflection;

using AwesomeAssertions;

using NetArchTest.Rules;

using Xunit;

namespace BareWire.ContractTests;

/// <summary>
/// Verifies that the package dependency rules defined in CONSTITUTION.md §6 are upheld.
/// Each test loads the relevant assembly and asserts it does not reference forbidden packages.
/// </summary>
public sealed class ArchitectureRuleTests
{
    /// <summary>
    /// Sub-namespaces unique to the BareWire (ex-Core) assembly.
    /// We cannot use <c>"BareWire"</c> as a blanket dependency check because NetArchTest
    /// uses prefix matching, which would also match <c>BareWire.Abstractions.*</c>.
    /// </summary>
    private static readonly string[] CoreNamespaces =
    [
        "BareWire.Bus",
        "BareWire.Pipeline",
        "BareWire.FlowControl",
        "BareWire.Configuration",
        "BareWire.Buffers",
    ];

    // -------------------------------------------------------------------------
    // Rule 1: Abstractions must NOT depend on any other BareWire package
    // -------------------------------------------------------------------------

    [Fact]
    public void Abstractions_ShouldNotDependOn_AnyBareWirePackage()
    {
        var assembly = typeof(BareWire.Abstractions.IBus).Assembly;

        string[] forbidden =
        [
            .. CoreNamespaces,
            "BareWire.Transport.AWS.SQS",
            "BareWire.Transport.Google.PubSub",
            "BareWire.Transport.RabbitMQ",
            "BareWire.Transport.Kafka",
            "BareWire.Transport.AzureServiceBus",
            "BareWire.Observability",
            "BareWire.Saga",
            "BareWire.Outbox",
            "BareWire.Serialization.Json",
            "BareWire.Serialization.MsgPack",
            "BareWire.Testing",
            "BareWire.Interop.MassTransit",
        ];

        foreach (var dep in forbidden)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(dep)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
        }
    }

    // -------------------------------------------------------------------------
    // Rule 2a: Core must NOT depend on Transport
    // -------------------------------------------------------------------------

    [Fact]
    public void Core_ShouldNotDependOn_Transport()
    {
        var assembly = typeof(BareWire.ServiceCollectionExtensions).Assembly;

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 2b: Core must NOT depend on Observability
    // -------------------------------------------------------------------------

    [Fact]
    public void Core_ShouldNotDependOn_Observability()
    {
        var assembly = typeof(BareWire.ServiceCollectionExtensions).Assembly;

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 3: Serialization.Json must NOT depend on Core or Transport
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialization_ShouldNotDependOn_CoreOrTransport()
    {
        var assembly = GetAssembly("BareWire.Serialization.Json");

        AssertNoDependencyOnCore(assembly);

        var resultTransport = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultTransport.IsSuccessful.Should().BeTrue(
            resultTransport.FailingTypeNames is { Count: > 0 } tNames ? tNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 3b: Serialization.MsgPack must NOT depend on Core or Transport
    // -------------------------------------------------------------------------

    [Fact]
    public void MsgPack_ShouldNotDependOn_CoreOrTransport()
    {
        var assembly = GetAssembly("BareWire.Serialization.MsgPack");

        AssertNoDependencyOnCore(assembly);

        string[] forbiddenTransports =
        [
            "BareWire.Transport.RabbitMQ",
            "BareWire.Transport.Kafka",
            "BareWire.Transport.AzureServiceBus",
        ];

        foreach (var dep in forbiddenTransports)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(dep)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
        }
    }

    // -------------------------------------------------------------------------
    // Rule 4: Transport.RabbitMQ must NOT depend on Core or Observability
    // -------------------------------------------------------------------------

    [Fact]
    public void Transport_ShouldNotDependOn_CoreOrObservability()
    {
        var assembly = GetAssembly("BareWire.Transport.RabbitMQ");

        AssertNoDependencyOnCore(assembly);

        var resultObservability = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        resultObservability.IsSuccessful.Should().BeTrue(
            resultObservability.FailingTypeNames is { Count: > 0 } oNames ? oNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 4b: Transport.Kafka must NOT depend on Core, Observability, or other Transports
    // -------------------------------------------------------------------------

    [Fact]
    public void KafkaTransport_ShouldNotDependOn_CoreOrObservability()
    {
        var assembly = GetAssembly("BareWire.Transport.Kafka");

        AssertNoDependencyOnCore(assembly);

        var resultObservability = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        resultObservability.IsSuccessful.Should().BeTrue(
            resultObservability.FailingTypeNames is { Count: > 0 } oNames ? oNames[0] : null);

        var resultRabbitMq = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultRabbitMq.IsSuccessful.Should().BeTrue(
            resultRabbitMq.FailingTypeNames is { Count: > 0 } rNames ? rNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 4c: Transport.AzureServiceBus must NOT depend on Core, Observability, or other Transports
    // -------------------------------------------------------------------------

    [Fact]
    public void AzureServiceBusTransport_ShouldNotDependOn_CoreOrObservability()
    {
        var assembly = GetAssembly("BareWire.Transport.AzureServiceBus");

        AssertNoDependencyOnCore(assembly);

        var resultObservability = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        resultObservability.IsSuccessful.Should().BeTrue(
            resultObservability.FailingTypeNames is { Count: > 0 } oNames ? oNames[0] : null);

        var resultRabbitMq = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultRabbitMq.IsSuccessful.Should().BeTrue(
            resultRabbitMq.FailingTypeNames is { Count: > 0 } rNames ? rNames[0] : null);

        var resultKafka = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.Kafka")
            .GetResult();

        resultKafka.IsSuccessful.Should().BeTrue(
            resultKafka.FailingTypeNames is { Count: > 0 } kNames ? kNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 4d: Transport.AWS.SQS must NOT depend on Core, Observability, or other Transports
    // -------------------------------------------------------------------------

    [Fact]
    public void SqsTransport_ShouldNotDependOn_CoreOrObservability()
    {
        var assembly = GetAssembly("BareWire.Transport.AWS.SQS");

        AssertNoDependencyOnCore(assembly);

        string[] forbidden =
        [
            "BareWire.Observability",
            "BareWire.Transport.RabbitMQ",
            "BareWire.Transport.Kafka",
            "BareWire.Transport.AzureServiceBus",
        ];

        foreach (var dep in forbidden)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(dep)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
        }
    }

    // -------------------------------------------------------------------------
    // Rule 4e: Transport.Google.PubSub must NOT depend on Core, Observability, or other Transports
    // -------------------------------------------------------------------------

    [Fact]
    public void PubSubTransport_ShouldNotDependOn_CoreOrObservability()
    {
        var assembly = GetAssembly("BareWire.Transport.Google.PubSub");

        AssertNoDependencyOnCore(assembly);

        string[] forbidden =
        [
            "BareWire.Observability",
            "BareWire.Transport.RabbitMQ",
            "BareWire.Transport.Kafka",
            "BareWire.Transport.AzureServiceBus",
            "BareWire.Transport.AWS.SQS",
        ];

        foreach (var dep in forbidden)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(dep)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
        }
    }

    // -------------------------------------------------------------------------
    // Rule 5: Saga must NOT depend on Transport or Observability
    // -------------------------------------------------------------------------

    [Fact]
    public void Saga_ShouldNotDependOn_TransportOrObservability()
    {
        var assembly = GetAssembly("BareWire.Saga");

        var resultTransport = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultTransport.IsSuccessful.Should().BeTrue(
            resultTransport.FailingTypeNames is { Count: > 0 } tNames ? tNames[0] : null);

        var resultObservability = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        resultObservability.IsSuccessful.Should().BeTrue(
            resultObservability.FailingTypeNames is { Count: > 0 } oNames ? oNames[0] : null);

        // GAP-2: explicitly guard against BareWire.Saga accidentally gaining a direct reference
        // to any concrete transport project (enforces the INativeMessageScheduler probing pattern).
        var resultAsb = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.AzureServiceBus")
            .GetResult();

        resultAsb.IsSuccessful.Should().BeTrue(
            resultAsb.FailingTypeNames is { Count: > 0 } aNames ? aNames[0] : null);

        var resultKafka = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.Kafka")
            .GetResult();

        resultKafka.IsSuccessful.Should().BeTrue(
            resultKafka.FailingTypeNames is { Count: > 0 } kNames ? kNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 6: Saga.EntityFramework must NOT depend on Transport or Observability
    // -------------------------------------------------------------------------

    [Fact]
    public void SagaEf_ShouldNotDependOn_TransportOrObservability()
    {
        var assembly = typeof(BareWire.Saga.EntityFramework.SagaDbContext).Assembly;

        var resultTransport = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultTransport.IsSuccessful.Should().BeTrue(
            resultTransport.FailingTypeNames is { Count: > 0 } tNames ? tNames[0] : null);

        var resultObservability = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        resultObservability.IsSuccessful.Should().BeTrue(
            resultObservability.FailingTypeNames is { Count: > 0 } oNames ? oNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 7: Outbox must NOT depend on Transport or Observability
    // -------------------------------------------------------------------------

    [Fact]
    public void Outbox_ShouldNotDependOn_TransportOrObservability()
    {
        var assembly = GetAssembly("BareWire.Outbox");

        var resultTransport = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultTransport.IsSuccessful.Should().BeTrue(
            resultTransport.FailingTypeNames is { Count: > 0 } tNames ? tNames[0] : null);

        var resultObservability = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        resultObservability.IsSuccessful.Should().BeTrue(
            resultObservability.FailingTypeNames is { Count: > 0 } oNames ? oNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 7b: Saga.Redis must NOT depend on Transport or Observability
    // -------------------------------------------------------------------------

    [Fact]
    public void SagaRedis_ShouldNotDependOn_TransportOrObservability()
    {
        var assembly = GetAssembly("BareWire.Saga.Redis");

        var resultTransport = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultTransport.IsSuccessful.Should().BeTrue(
            resultTransport.FailingTypeNames is { Count: > 0 } tNames ? tNames[0] : null);

        var resultObservability = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        resultObservability.IsSuccessful.Should().BeTrue(
            resultObservability.FailingTypeNames is { Count: > 0 } oNames ? oNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 8: Outbox.EntityFramework must NOT depend on Transport or Observability
    // -------------------------------------------------------------------------

    [Fact]
    public void OutboxEf_ShouldNotDependOn_TransportOrObservability()
    {
        var assembly = typeof(BareWire.Outbox.EntityFramework.OutboxDbContext).Assembly;

        var resultTransport = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultTransport.IsSuccessful.Should().BeTrue(
            resultTransport.FailingTypeNames is { Count: > 0 } tNames ? tNames[0] : null);

        var resultObservability = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Observability")
            .GetResult();

        resultObservability.IsSuccessful.Should().BeTrue(
            resultObservability.FailingTypeNames is { Count: > 0 } oNames ? oNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 8b: Outbox.EntityFramework must stay provider-agnostic — no Npgsql
    // ASSEMBLY reference. PostgreSQL specifics (the FOR UPDATE SKIP LOCKED claim
    // and the PerKey NOT EXISTS predicate) live only in PostgresOutboxSqlDialect,
    // which selects the provider by the "Npgsql.EntityFrameworkCore.PostgreSQL"
    // string literal (ProviderName) — never by referencing an Npgsql type. This
    // guards against leaking a provider implementation into a general class (R7.7).
    //
    // The check is on the assembly-reference graph, NOT NetArchTest's
    // HaveDependencyOn("Npgsql"): the latter matches the ldstr provider-name string
    // constant (a false positive on the legitimate provider-agnostic pattern), while
    // GetReferencedAssemblies sees only real binary dependencies.
    // -------------------------------------------------------------------------

    [Fact]
    public void OutboxEf_ShouldNotReference_NpgsqlAssembly()
    {
        var assembly = typeof(BareWire.Outbox.EntityFramework.OutboxDbContext).Assembly;

        var npgsqlReferences = assembly.GetReferencedAssemblies()
            .Where(a => a.Name is not null
                && a.Name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name!)
            .ToArray();

        npgsqlReferences.Should().BeEmpty(
            "Outbox.EntityFramework must stay provider-agnostic — PostgreSQL specifics belong only in "
            + "PostgresOutboxSqlDialect (selected by the ProviderName string), not via an Npgsql binary "
            + "dependency; found: {0}",
            string.Join(", ", npgsqlReferences));
    }

    // -------------------------------------------------------------------------
    // Rule 8c: Transport.RabbitMQ must reproduce the MassTransit Namespace:TypeName
    // exchange-naming convention ITSELF (via its own RequestExchangeNameFormatter)
    // and must NOT take a binary reference on the BareWire.Interop.MassTransit
    // ASSEMBLY. The publish-style request/response routing reuses the MassTransit
    // urn:message: / colon convention purely as string constants — never by linking
    // the interop package, which would leak the envelope/convention implementation
    // into the transport (Feature 14, task 14.1 + 14.15).
    //
    // The check is on the assembly-reference graph, NOT NetArchTest's
    // HaveDependencyOn("MassTransit"): the latter matches the ldstr "urn:message:" /
    // colon-convention string constants (a false positive on the legitimate
    // self-reproduced convention), while GetReferencedAssemblies sees only real
    // binary dependencies.
    // -------------------------------------------------------------------------

    [Fact]
    public void TransportRabbitMq_ShouldNotReference_InteropMassTransitAssembly()
    {
        var assembly = typeof(BareWire.Transport.RabbitMQ.ServiceCollectionExtensions).Assembly;

        var interopReferences = assembly.GetReferencedAssemblies()
            .Where(a => a.Name is not null
                && a.Name.Equals("BareWire.Interop.MassTransit", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name!)
            .ToArray();

        interopReferences.Should().BeEmpty(
            "Transport.RabbitMQ must reproduce the MassTransit Namespace:TypeName convention itself "
            + "(via its own RequestExchangeNameFormatter, task 14.1) without a binary reference to "
            + "BareWire.Interop.MassTransit; found: {0}",
            string.Join(", ", interopReferences));
    }

    // -------------------------------------------------------------------------
    // Rule 9: Observability must NOT depend on Core or Transport
    // -------------------------------------------------------------------------

    [Fact]
    public void Observability_ShouldNotDependOn_CoreOrTransport()
    {
        var assembly = typeof(BareWire.Observability.IObservabilityConfigurator).Assembly;

        AssertNoDependencyOnCore(assembly);

        var resultTransport = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        resultTransport.IsSuccessful.Should().BeTrue(
            resultTransport.FailingTypeNames is { Count: > 0 } tNames ? tNames[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 10: Testing must NOT depend on production Transport
    // -------------------------------------------------------------------------

    [Fact]
    public void Testing_ShouldNotDependOn_ProductionTransport()
    {
        var assembly = typeof(BareWire.Testing.BareWireTestHarness).Assembly;

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("BareWire.Transport.RabbitMQ")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 11: Interop.MassTransit must NOT depend on Core, Transport, Observability, Saga, Outbox, or Testing
    // -------------------------------------------------------------------------

    [Fact]
    public void Rule11_InteropMassTransit_ShouldNotDependOn_CoreTransportOrObservability()
    {
        var assembly = Assembly.Load("BareWire.Interop.MassTransit");

        AssertNoDependencyOnCore(assembly);

        string[] forbidden =
        [
            "BareWire.Transport.RabbitMQ",
            "BareWire.Observability",
            "BareWire.Saga",
            "BareWire.Outbox",
            "BareWire.Testing",
        ];

        foreach (var dep in forbidden)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(dep)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
        }
    }

    // -------------------------------------------------------------------------
    // Rule 12: CloudEvents must NOT depend on any BareWire package except Abstractions
    // -------------------------------------------------------------------------

    [Fact]
    public void CloudEvents_DependsOnlyOnAbstractions()
    {
        var assembly = typeof(BareWire.CloudEvents.ICloudEventAttributes).Assembly;

        AssertNoDependencyOnCore(assembly);

        string[] forbidden =
        [
            "BareWire.Transport.RabbitMQ",
            "BareWire.Observability",
            "BareWire.Saga",
            "BareWire.Outbox",
            "BareWire.Serialization.Json",
            "BareWire.Testing",
            "BareWire.Interop.MassTransit",
        ];

        foreach (var dep in forbidden)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(dep)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
        }
    }

    // -------------------------------------------------------------------------
    // Rule 13: CloudEvents (production) must NOT reference the CloudNative.CloudEvents SDK
    //          (allowed ONLY as a test-only oracle in interop tests — see task 13.15)
    // -------------------------------------------------------------------------

    [Fact]
    public void CloudEvents_DoesNotReferenceCloudNativeSdk()
    {
        var assembly = typeof(BareWire.CloudEvents.ICloudEventAttributes).Assembly;

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOn("CloudNative.CloudEvents")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
    }

    // -------------------------------------------------------------------------
    // Rule 14: Single-call transport bundle layer (Feature 15 — ADR-028).
    //
    // The five bundle packages (BareWire.{RabbitMQ|Kafka|AzureServiceBus|AWS.SQS|
    // Google.PubSub}) form a NEW, separate layer that references BOTH the Core
    // (BareWire) and the matching Transport (BareWire.Transport.*). The dependency
    // direction is strictly one-way: a bundle may reference Core + Transport, but
    // neither Core nor any Transport may reference a bundle. This proves the
    // single-call ergonomics did NOT loosen the Core⊥Transport invariant — the
    // bundle is an extra layer on top, not a back-edge.
    // -------------------------------------------------------------------------

    /// <summary>Bundle assembly → the Transport assembly it must wrap.</summary>
    private static readonly (Assembly Bundle, string TransportName)[] Bundles =
    [
        (typeof(BareWire.RabbitMQ.ServiceCollectionExtensions).Assembly, "BareWire.Transport.RabbitMQ"),
        (typeof(BareWire.Kafka.ServiceCollectionExtensions).Assembly, "BareWire.Transport.Kafka"),
        (typeof(BareWire.AzureServiceBus.ServiceCollectionExtensions).Assembly, "BareWire.Transport.AzureServiceBus"),
        (typeof(BareWire.AWS.SQS.ServiceCollectionExtensions).Assembly, "BareWire.Transport.AWS.SQS"),
        (typeof(BareWire.Google.PubSub.ServiceCollectionExtensions).Assembly, "BareWire.Transport.Google.PubSub"),
    ];

    private static readonly string[] BundleNames =
    [
        "BareWire.RabbitMQ",
        "BareWire.Kafka",
        "BareWire.AzureServiceBus",
        "BareWire.AWS.SQS",
        "BareWire.Google.PubSub",
    ];

    // Rule 14a: each bundle references BOTH the Core and its matching Transport.
    [Fact]
    public void Bundle_ShouldDependOn_BothCoreAndTransport()
    {
        foreach (var (bundle, transportName) in Bundles)
        {
            var referenced = bundle.GetReferencedAssemblies()
                .Select(a => a.Name)
                .Where(n => n is not null)
                .ToArray();

            referenced.Should().Contain(
                "BareWire",
                "the bundle {0} must reference the Core (BareWire) — it calls AddBareWire", bundle.GetName().Name!);

            referenced.Should().Contain(
                transportName,
                "the bundle {0} must reference its transport ({1}) — it calls the transport's AddBareWire registration method",
                bundle.GetName().Name!, transportName);
        }
    }

    // Rule 14b: the Core (BareWire) must NOT reference any bundle (one-directionality).
    [Fact]
    public void Core_ShouldNotDependOn_AnyBundle()
    {
        var core = typeof(BareWire.ServiceCollectionExtensions).Assembly;

        foreach (var bundleName in BundleNames)
        {
            var result = Types.InAssembly(core)
                .ShouldNot()
                .HaveDependencyOn(bundleName)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);

            core.GetReferencedAssemblies().Select(a => a.Name)
                .Should().NotContain(bundleName,
                    "Core must never take a binary reference on the bundle layer ({0})", bundleName);
        }
    }

    // Rule 14c: no Transport assembly may reference any bundle (one-directionality).
    [Fact]
    public void Transports_ShouldNotDependOn_AnyBundle()
    {
        string[] transports =
        [
            "BareWire.Transport.RabbitMQ",
            "BareWire.Transport.Kafka",
            "BareWire.Transport.AzureServiceBus",
            "BareWire.Transport.AWS.SQS",
            "BareWire.Transport.Google.PubSub",
        ];

        foreach (var transportName in transports)
        {
            var transport = GetAssembly(transportName);
            var referenced = transport.GetReferencedAssemblies().Select(a => a.Name).ToArray();

            foreach (var bundleName in BundleNames)
            {
                referenced.Should().NotContain(bundleName,
                    "transport {0} must never reference the bundle layer ({1}) — the bundle wraps the transport, not the other way round",
                    transportName, bundleName);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void AssertNoDependencyOnCore(Assembly assembly)
    {
        foreach (var ns in CoreNamespaces)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(ns)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                result.FailingTypeNames is { Count: > 0 } names ? names[0] : null);
        }
    }

    private static Assembly GetAssembly(string name) => Assembly.Load(name);
}
