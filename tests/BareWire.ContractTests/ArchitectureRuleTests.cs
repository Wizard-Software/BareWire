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
