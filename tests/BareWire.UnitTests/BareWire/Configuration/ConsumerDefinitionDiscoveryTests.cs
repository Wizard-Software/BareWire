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
}
