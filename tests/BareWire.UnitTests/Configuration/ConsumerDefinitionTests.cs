using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using NSubstitute;

namespace BareWire.UnitTests.Configuration;

/// <summary>
/// Unit tests for the single-parameter <see cref="ConsumerDefinition{TConsumer}"/> base type and the hoisted
/// <see cref="IConsumerConfigurator{TConsumer}"/> façade: derivation, overridability of the default no-op
/// <c>Configure</c>, the non-breaking inheritance of the two-parameter configurator, and the façade's
/// message-agnostic method surface.
/// </summary>
public sealed class ConsumerDefinitionTests
{
    // A plain reference type that does NOT implement IConsumer<T>, proving the single-parameter base only
    // requires `where TConsumer : class`.
    private sealed class TestConsumer;

    private sealed record TestMessage(string Value);

    // Implements IConsumer<TMessage> so the two-parameter configurator can be constructed over it.
    private sealed class TestConsumerImpl : IConsumer<TestMessage>
    {
        public Task ConsumeAsync(ConsumeContext<TestMessage> context) => Task.CompletedTask;
    }

    // Overrides Configure and records that it ran; exposes a public hook so the protected method is reachable.
    private sealed class OverridingDefinition : ConsumerDefinition<TestConsumer>
    {
        public bool Configured { get; private set; }

        protected override void Configure(
            IReceiveEndpointConfigurator endpoint,
            IConsumerConfigurator<TestConsumer> consumer)
        {
            Configured = true;
        }

        public void InvokeConfigure(
            IReceiveEndpointConfigurator endpoint,
            IConsumerConfigurator<TestConsumer> consumer) => Configure(endpoint, consumer);
    }

    // Does NOT override Configure; exposes a public hook to invoke the inherited default no-op.
    private sealed class DefaultDefinition : ConsumerDefinition<TestConsumer>
    {
        public void InvokeConfigure(
            IReceiveEndpointConfigurator endpoint,
            IConsumerConfigurator<TestConsumer> consumer) => Configure(endpoint, consumer);
    }

    // Hand-rolled no-op façade stub. A manual implementation avoids NSubstitute/Castle DynamicProxy, which
    // cannot proxy IConsumerConfigurator<TestConsumer> because the private nested TestConsumer type argument
    // is inaccessible to the dynamic proxy assembly. The code under test never inspects this argument.
    private sealed class StubConsumerConfigurator : IConsumerConfigurator<TestConsumer>
    {
        public void RoutingKey(string routingKey)
        {
        }

        public void RoutingKeys(params string[] routingKeys)
        {
        }

        public void AcceptUntyped()
        {
        }

        public void UseMassTransitEnvelope()
        {
        }

        public void Retry(Action<IRetryConfigurator> configure)
        {
        }
    }

    [Fact]
    public void Configure_WhenOverridden_IsInvoked()
    {
        // Arrange
        var definition = new OverridingDefinition();
        var endpoint = Substitute.For<IReceiveEndpointConfigurator>();
        var consumer = new StubConsumerConfigurator();

        // Act
        definition.InvokeConfigure(endpoint, consumer);

        // Assert
        definition.Configured.Should().BeTrue();
    }

    [Fact]
    public void Configure_WhenNotOverridden_DefaultIsNoOp()
    {
        // Arrange
        var definition = new DefaultDefinition();
        var endpoint = Substitute.For<IReceiveEndpointConfigurator>();
        var consumer = new StubConsumerConfigurator();

        // Act
        var act = () => definition.InvokeConfigure(endpoint, consumer);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void TwoParamConfigurator_IsAssignableTo_SingleParamFacade()
    {
        // Assert — the two-parameter configurator inherits the single-parameter façade (non-breaking hoist).
        typeof(IConsumerConfigurator<TestConsumerImpl, TestMessage>)
            .Should().BeAssignableTo<IConsumerConfigurator<TestConsumerImpl>>();
    }

    [Theory]
    [InlineData("RoutingKey")]
    [InlineData("RoutingKeys")]
    [InlineData("AcceptUntyped")]
    [InlineData("UseMassTransitEnvelope")]
    public void SingleParamFacade_ExposesFourMessageAgnosticMethods(string methodName)
    {
        // Arrange
        var facade = typeof(IConsumerConfigurator<>);

        // Assert — the façade declares each of the four message-agnostic methods directly.
        facade.GetMethod(methodName).Should().NotBeNull(
            "the single-parameter façade must declare the four message-agnostic methods");
    }
}
