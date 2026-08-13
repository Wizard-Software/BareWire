using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Configuration;

namespace BareWire.UnitTests.Core.Configuration;

/// <summary>
/// Tests for the startup <c>TMessage</c> inference behind the sugar overload
/// <see cref="global::BareWire.Abstractions.Configuration.IReceiveEndpointConfigurator.Consumer{TConsumer}()"/>
/// (task 19.6). A consumer implementing exactly one <see cref="IConsumer{T}"/> has its message type
/// inferred once at configuration time and materialized into a <c>ConsumerRegistration</c> identical to
/// the explicit <c>Consumer&lt;TConsumer, TMessage&gt;()</c> path; a consumer implementing none or several
/// fails fast with an actionable <see cref="BareWireConfigurationException"/>.
/// </summary>
public sealed class ConsumerMessageTypeInferenceTests
{
    private sealed record OrderPlaced(string Id);

    private sealed record OrderShipped(string Id);

    private sealed class SingleConsumer : IConsumer<OrderPlaced>
    {
        public Task ConsumeAsync(ConsumeContext<OrderPlaced> context) => Task.CompletedTask;
    }

    private sealed class MultiConsumer : IConsumer<OrderPlaced>, IConsumer<OrderShipped>
    {
        public Task ConsumeAsync(ConsumeContext<OrderPlaced> context) => Task.CompletedTask;

        public Task ConsumeAsync(ConsumeContext<OrderShipped> context) => Task.CompletedTask;
    }

    private sealed class NotAConsumer
    {
    }

    [Fact]
    public void Consumer_SingleIConsumer_InfersMessageTypeAtStartup()
    {
        var configuration = new ReceiveEndpointConfiguration("orders");

        configuration.Consumer<SingleConsumer>();

        configuration.ConsumerRegistrations.Should().ContainSingle();
        var registration = configuration.ConsumerRegistrations[0];
        registration.ConsumerType.Should().Be<SingleConsumer>();
        registration.MessageType.Should().Be<OrderPlaced>();
        configuration.ConsumerTypes.Should().ContainSingle().Which.Should().Be<SingleConsumer>();
    }

    [Fact]
    public void Consumer_SingleIConsumer_ProducesSameRegistrationAsExplicitOverload()
    {
        var inferred = new ReceiveEndpointConfiguration("orders");
        inferred.Consumer<SingleConsumer>();

        var explicitlyTyped = new ReceiveEndpointConfiguration("orders");
        explicitlyTyped.Consumer<SingleConsumer, OrderPlaced>();

        inferred.ConsumerRegistrations[0].Should().Be(explicitlyTyped.ConsumerRegistrations[0]);
    }

    [Fact]
    public void Consumer_MultipleIConsumer_FailsFastRequestingExplicitOverload()
    {
        var configuration = new ReceiveEndpointConfiguration("orders");

        var act = () => configuration.Consumer<MultiConsumer>();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.Message.Should().Contain("Consumer<MultiConsumer, TMessage>");
        configuration.ConsumerRegistrations.Should().BeEmpty();
    }

    [Fact]
    public void Consumer_NoIConsumer_FailsFast()
    {
        var configuration = new ReceiveEndpointConfiguration("orders");

        var act = () => configuration.Consumer<NotAConsumer>();

        act.Should().Throw<BareWireConfigurationException>();
        configuration.ConsumerRegistrations.Should().BeEmpty();
    }
}
