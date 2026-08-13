using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;

namespace BareWire.UnitTests.Configuration;

/// <summary>
/// Contract tests for <see cref="IConsumerConfigurator{TConsumer, TMessage}"/> — asserts the published
/// shape (public interface, three <see langword="void"/> methods, namespace, and the two type-parameter
/// constraints). Mirrors <see cref="IPublishConfiguratorContractTests"/> for the consume-side configurator.
/// Guards the house "void, not fluent" configurator convention and the explicit acceptance criteria of the
/// task against regression: the tests fail if a method is changed to a fluent (<c>return this</c>) signature,
/// the <c>params</c> modifier is dropped from <c>RoutingKeys</c>, or either generic constraint
/// (<c>where TConsumer : class, IConsumer&lt;TMessage&gt;</c> / <c>where TMessage : class</c>) is removed.
/// </summary>
public sealed class IConsumerConfiguratorContractTests
{
    private static readonly Type ConfiguratorType = typeof(IConsumerConfigurator<,>);

    // The four message-agnostic methods were hoisted to the single-parameter façade; Type.GetMethod on an
    // interface does not traverse base interfaces, so method-shape assertions target the façade directly.
    private static readonly Type FacadeType = typeof(IConsumerConfigurator<>);

    [Fact]
    public void IConsumerConfigurator_IsPublicInterface()
    {
        // Assert
        ConfiguratorType.IsInterface.Should().BeTrue();
        ConfiguratorType.IsPublic.Should().BeTrue();
    }

    [Fact]
    public void IConsumerConfigurator_LivesInAbstractionsConfigurationNamespace()
    {
        // Assert
        ConfiguratorType.Namespace.Should().Be("BareWire.Abstractions.Configuration");
    }

    [Fact]
    public void TMessage_GenericParameter_HasReferenceTypeConstraint()
    {
        // Arrange
        var tMessage = ConfiguratorType.GetGenericArguments()[1];

        // Assert
        tMessage.GenericParameterAttributes
            .HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)
            .Should().BeTrue();
    }

    [Fact]
    public void TConsumer_GenericParameter_HasReferenceTypeConstraint()
    {
        // Arrange
        var tConsumer = ConfiguratorType.GetGenericArguments()[0];

        // Assert
        tConsumer.GenericParameterAttributes
            .HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)
            .Should().BeTrue();
    }

    [Fact]
    public void TConsumer_GenericParameter_IsConstrainedToIConsumerOfTMessage()
    {
        // Arrange
        var tConsumer = ConfiguratorType.GetGenericArguments()[0];

        // Act — the IConsumer<TMessage> interface constraint (constructed over the second type parameter).
        var hasConsumerConstraint = tConsumer.GetGenericParameterConstraints()
            .Any(c => c.IsGenericType && c.GetGenericTypeDefinition() == typeof(IConsumer<>));

        // Assert
        hasConsumerConstraint.Should().BeTrue();
    }

    [Fact]
    public void RoutingKey_IsVoidMethod_TakingSingleStringParameter()
    {
        // Arrange
        var method = FacadeType.GetMethod("RoutingKey");

        // Assert
        method.Should().NotBeNull();
        (method!.ReturnType == typeof(void)).Should().BeTrue();
        method.GetParameters().Should().ContainSingle()
            .Which.ParameterType.Should().Be<string>();
    }

    [Fact]
    public void RoutingKeys_IsVoidMethod_TakingSingleParamsStringArrayParameter()
    {
        // Arrange
        var method = FacadeType.GetMethod("RoutingKeys");

        // Assert
        method.Should().NotBeNull();
        (method!.ReturnType == typeof(void)).Should().BeTrue();
        var parameter = method.GetParameters().Should().ContainSingle().Subject;
        parameter.ParameterType.Should().Be<string[]>();
        parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false)
            .Should().BeTrue("RoutingKeys must be declared with the params modifier");
    }

    [Fact]
    public void AcceptUntyped_IsVoidMethod_TakingNoParameters()
    {
        // Arrange
        var method = FacadeType.GetMethod("AcceptUntyped");

        // Assert
        method.Should().NotBeNull();
        (method!.ReturnType == typeof(void)).Should().BeTrue();
        method.GetParameters().Should().BeEmpty();
    }
}
