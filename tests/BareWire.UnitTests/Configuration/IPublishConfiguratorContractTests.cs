using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions.Configuration;

namespace BareWire.UnitTests.Configuration;

/// <summary>
/// Contract tests for <see cref="IPublishConfigurator{T}"/> — asserts the published shape
/// (public interface, two <see langword="void"/> methods each taking a single string,
/// reference-type constraint on the type parameter, and namespace). Guards the house
/// "void, not fluent" configurator convention and the explicit acceptance criteria of the
/// task against regression: the tests fail if a method is changed to a fluent
/// (<c>return this</c>) signature or the <c>where T : class</c> constraint is dropped.
/// </summary>
public sealed class IPublishConfiguratorContractTests
{
    private static readonly Type ConfiguratorType = typeof(IPublishConfigurator<>);

    [Fact]
    public void IPublishConfigurator_IsPublicInterface()
    {
        // Assert
        ConfiguratorType.IsInterface.Should().BeTrue();
        ConfiguratorType.IsPublic.Should().BeTrue();
    }

    [Fact]
    public void IPublishConfigurator_LivesInAbstractionsConfigurationNamespace()
    {
        // Assert
        ConfiguratorType.Namespace.Should().Be("BareWire.Abstractions.Configuration");
    }

    [Fact]
    public void IPublishConfigurator_GenericParameter_HasReferenceTypeConstraint()
    {
        // Arrange
        var typeParameter = ConfiguratorType.GetGenericArguments()[0];

        // Assert
        typeParameter.GenericParameterAttributes
            .HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)
            .Should().BeTrue();
    }

    [Fact]
    public void Exchange_IsVoidMethod_TakingSingleStringParameter()
    {
        // Arrange
        var method = ConfiguratorType.GetMethod("Exchange");

        // Assert
        method.Should().NotBeNull();
        (method!.ReturnType == typeof(void)).Should().BeTrue();
        method.GetParameters().Should().ContainSingle()
            .Which.ParameterType.Should().Be<string>();
    }

    [Fact]
    public void RoutingKey_IsVoidMethod_TakingSingleStringParameter()
    {
        // Arrange
        var method = ConfiguratorType.GetMethod("RoutingKey");

        // Assert
        method.Should().NotBeNull();
        (method!.ReturnType == typeof(void)).Should().BeTrue();
        method.GetParameters().Should().ContainSingle()
            .Which.ParameterType.Should().Be<string>();
    }
}
