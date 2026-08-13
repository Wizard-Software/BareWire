using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Bus;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.UnitTests.Core.Configuration;

/// <summary>
/// Tests for DI-driven <c>ConsumerDefinition&lt;TConsumer&gt;</c> discovery at start-up (task 19.7). A
/// definition registered in the container is read once at start-up and its per-consumer settings applied and
/// merged into the existing <see cref="ConsumerRegistration"/>; a definition that exists in the assembly but
/// is not registered in DI is never applied — discovery is registration-driven, not an assembly scan.
/// </summary>
public sealed class ConsumerDefinitionDiscoveryTests
{
    private sealed record OrderPlaced(string Id);

    private sealed class OrderConsumer : IConsumer<OrderPlaced>
    {
        public Task ConsumeAsync(ConsumeContext<OrderPlaced> context) => Task.CompletedTask;
    }

    private sealed class OrderDefinition : ConsumerDefinition<OrderConsumer>
    {
        protected override void Configure(
            IReceiveEndpointConfigurator endpoint,
            IConsumerConfigurator<OrderConsumer> consumer)
        {
            consumer.RoutingKey("orders.*");
            consumer.AcceptUntyped();
            consumer.UseMassTransitEnvelope();
        }
    }

    private static ConsumerRegistration Reg() => new(typeof(OrderConsumer), typeof(OrderPlaced));

    [Fact]
    public void RegisteredDefinition_ReadAtStartup_AppliesPerConsumerSettings()
    {
        var services = new ServiceCollection()
            .AddSingleton<ConsumerDefinition<OrderConsumer>, OrderDefinition>()
            .BuildServiceProvider();

        var result = ConsumerDefinitionDiscovery.ApplyRegisteredDefinitions([Reg()], services);

        result[0].RoutingKeys.Should().ContainSingle().Which.Should().Be("orders.*");
        result[0].AcceptUntyped.Should().BeTrue();
        result[0].UseMassTransitEnvelope.Should().BeTrue();
        result[0].MessageType.Should().Be<OrderPlaced>();
    }

    [Fact]
    public void UnregisteredDefinition_IsNotApplied()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        IReadOnlyList<ConsumerRegistration> input = [Reg()];

        var result = ConsumerDefinitionDiscovery.ApplyRegisteredDefinitions(input, services);

        result.Should().BeSameAs(input);
        result[0].RoutingKeys.Should().BeNull();
        result[0].AcceptUntyped.Should().BeFalse();
    }

    [Fact]
    public void Discovery_IsDiRegistrationDriven_NotAssemblyScan()
    {
        var empty = new ServiceCollection().BuildServiceProvider();
        ConsumerDefinitionDiscovery.ApplyRegisteredDefinitions([Reg()], empty)[0]
            .AcceptUntyped.Should().BeFalse();

        var registered = new ServiceCollection()
            .AddSingleton<ConsumerDefinition<OrderConsumer>, OrderDefinition>()
            .BuildServiceProvider();
        ConsumerDefinitionDiscovery.ApplyRegisteredDefinitions([Reg()], registered)[0]
            .AcceptUntyped.Should().BeTrue();
    }

    [Fact]
    public void Definition_UnionsRoutingKeys_WithExistingRegistration()
    {
        var services = new ServiceCollection()
            .AddSingleton<ConsumerDefinition<OrderConsumer>, OrderDefinition>()
            .BuildServiceProvider();
        ConsumerRegistration existing = Reg() with { RoutingKeys = ["existing.key"] };

        var result = ConsumerDefinitionDiscovery.ApplyRegisteredDefinitions([existing], services);

        result[0].RoutingKeys.Should().BeEquivalentTo(["existing.key", "orders.*"]);
    }

    [Fact]
    public void ApplyToEndpoints_MergesConsumers_AndPreservesEndpointFields()
    {
        var services = new ServiceCollection()
            .AddSingleton<ConsumerDefinition<OrderConsumer>, OrderDefinition>()
            .BuildServiceProvider();
        var endpoint = new EndpointBinding { EndpointName = "q", PrefetchCount = 32, Consumers = [Reg()] };

        var result = ConsumerDefinitionDiscovery.ApplyToEndpoints([endpoint], services);

        result[0].EndpointName.Should().Be("q");
        result[0].PrefetchCount.Should().Be(32);
        result[0].Consumers[0].AcceptUntyped.Should().BeTrue();
    }

    // A definition that tunes the endpoint through the `endpoint` argument of Configure.
    private sealed class EndpointTuningDefinition : ConsumerDefinition<OrderConsumer>
    {
        protected override void Configure(
            IReceiveEndpointConfigurator endpoint,
            IConsumerConfigurator<OrderConsumer> consumer)
        {
            endpoint.PrefetchCount = 64;
            endpoint.ConcurrentMessageLimit = 4;
            endpoint.RetryCount = 5;
            endpoint.RetryInterval = TimeSpan.FromSeconds(2);
        }
    }

    // A definition that (illegally) tries to register another consumer on the endpoint.
    private sealed class NestedRegistrationDefinition : ConsumerDefinition<OrderConsumer>
    {
        protected override void Configure(
            IReceiveEndpointConfigurator endpoint,
            IConsumerConfigurator<OrderConsumer> consumer)
            => endpoint.Consumer<OrderConsumer, OrderPlaced>();
    }

    // A definition that (illegally) tries to configure endpoint ordering.
    private sealed class EndpointOrderingDefinition : ConsumerDefinition<OrderConsumer>
    {
        protected override void Configure(
            IReceiveEndpointConfigurator endpoint,
            IConsumerConfigurator<OrderConsumer> consumer)
            => endpoint.OrderedByHeader("tenant");
    }

    [Fact]
    public void ApplyToEndpoints_MaterializesEndpointLevelSettings_FromDefinition()
    {
        var services = new ServiceCollection()
            .AddSingleton<ConsumerDefinition<OrderConsumer>, EndpointTuningDefinition>()
            .BuildServiceProvider();
        var endpoint = new EndpointBinding { EndpointName = "q", Consumers = [Reg()] };

        var result = ConsumerDefinitionDiscovery.ApplyToEndpoints([endpoint], services);

        result[0].PrefetchCount.Should().Be(64);
        result[0].ConcurrentMessageLimit.Should().Be(4);
        result[0].RetryCount.Should().Be(5);
        result[0].RetryInterval.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ApplyToEndpoints_DefinitionRegisteringConsumerOnEndpoint_ThrowsNotSupported()
    {
        var services = new ServiceCollection()
            .AddSingleton<ConsumerDefinition<OrderConsumer>, NestedRegistrationDefinition>()
            .BuildServiceProvider();
        var endpoint = new EndpointBinding { EndpointName = "q", Consumers = [Reg()] };

        var act = () => ConsumerDefinitionDiscovery.ApplyToEndpoints([endpoint], services);

        act.Should().Throw<NotSupportedException>().WithMessage("*cannot be configured from ConsumerDefinition*");
    }

    [Fact]
    public void ApplyToEndpoints_DefinitionConfiguringEndpointOrdering_ThrowsNotSupported()
    {
        var services = new ServiceCollection()
            .AddSingleton<ConsumerDefinition<OrderConsumer>, EndpointOrderingDefinition>()
            .BuildServiceProvider();
        var endpoint = new EndpointBinding { EndpointName = "q", Consumers = [Reg()] };

        var act = () => ConsumerDefinitionDiscovery.ApplyToEndpoints([endpoint], services);

        act.Should().Throw<NotSupportedException>().WithMessage("*cannot be configured from ConsumerDefinition*");
    }
}
