using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Google.PubSub.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubServiceCollectionExtensionsTests
{
    private sealed record OrderCreated(string OrderId);

    private sealed class TestConsumer : IConsumer<OrderCreated>
    {
        public Task ConsumeAsync(ConsumeContext<OrderCreated> context) => Task.CompletedTask;
    }

    private sealed class TestRawConsumer : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context) => Task.CompletedTask;
    }

    private static ServiceProvider Build(Action<IPubSubConfigurator> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWirePubSub(k =>
        {
            k.ProjectId("test-project");
            k.UseEmulator("localhost:8085");
            configure(k);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddBareWirePubSub_WithReceiveEndpoint_RegistersEndpointBinding()
    {
        using ServiceProvider sp = Build(k => k.ReceiveEndpoint("orders-sub", e => e.Consumer<TestConsumer, OrderCreated>()));

        IReadOnlyList<EndpointBinding> bindings = sp.GetRequiredService<IReadOnlyList<EndpointBinding>>();

        bindings.Should().ContainSingle();
        bindings[0].EndpointName.Should().Be("orders-sub");
        bindings[0].Consumers.Should().ContainSingle();
        bindings[0].Consumers[0].ConsumerType.Should().Be<TestConsumer>();
        bindings[0].Consumers[0].MessageType.Should().Be<OrderCreated>();
        bindings[0].DeadLetterExchange.Should().BeNull();
        bindings[0].DeadLetterRoutingKey.Should().BeNull();
    }

    [Fact]
    public void AddBareWirePubSub_WithEndpointSettings_MapsSettingsOnBinding()
    {
        using ServiceProvider sp = Build(k => k.ReceiveEndpoint("orders-sub", e =>
        {
            e.PrefetchCount = 32;
            e.ConcurrentMessageLimit = 4;
            e.RetryCount = 3;
            e.RetryInterval = TimeSpan.FromSeconds(1);
            e.RawConsumer<TestRawConsumer>();
            e.OrderedByHeader("tenant");
        }));

        EndpointBinding binding = sp.GetRequiredService<IReadOnlyList<EndpointBinding>>().Single();

        binding.PrefetchCount.Should().Be(32);
        binding.ConcurrentMessageLimit.Should().Be(4);
        binding.RetryCount.Should().Be(3);
        binding.RetryInterval.Should().Be(TimeSpan.FromSeconds(1));
        binding.RawConsumers.Should().Contain(typeof(TestRawConsumer));
        binding.Ordering.Should().NotBeNull();
        binding.Ordering!.HeaderName.Should().Be("tenant");
    }

    [Fact]
    public void AddBareWirePubSub_WithTwoReceiveEndpoints_RegistersBothInDeclarationOrder()
    {
        using ServiceProvider sp = Build(k =>
        {
            k.ReceiveEndpoint("a", e => e.Consumer<TestConsumer, OrderCreated>());
            k.ReceiveEndpoint("b", e => e.Consumer<TestConsumer, OrderCreated>());
        });

        IReadOnlyList<EndpointBinding> bindings = sp.GetRequiredService<IReadOnlyList<EndpointBinding>>();

        bindings.Select(static b => b.EndpointName).Should().Equal("a", "b");
    }

    [Fact]
    public void AddBareWirePubSub_WithoutReceiveEndpoint_RegistersEmptyBindingList()
    {
        using ServiceProvider sp = Build(static _ => { });

        IReadOnlyList<EndpointBinding>? bindings = sp.GetService<IReadOnlyList<EndpointBinding>>();

        bindings.Should().NotBeNull();
        bindings.Should().BeEmpty();
    }

    [Fact]
    public void ReceiveEndpoint_WithEmptyName_ThrowsArgumentException()
    {
        Action act = () => Build(k => k.ReceiveEndpoint(string.Empty, static e => e.Consumer<TestConsumer, OrderCreated>()));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReceiveEndpoint_WithNullConfigure_ThrowsArgumentNullException()
    {
        Action act = () => Build(k => k.ReceiveEndpoint("orders-sub", null!));

        act.Should().Throw<ArgumentNullException>();
    }
}
