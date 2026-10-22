using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddBareWireRabbitMq_RegistersITransportAdapter()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddBareWireRabbitMq(rmq =>
        {
            rmq.Host("amqp://guest:guest@localhost:5672/");
        });

        // Assert
        await using var provider = services.BuildServiceProvider();
        var adapter = provider.GetService<ITransportAdapter>();
        adapter.Should().NotBeNull();
        adapter.Should().BeOfType<RabbitMqTransportAdapter>();
    }

    [Fact]
    public void AddBareWireRabbitMq_CalledTwice_ThrowsConfigurationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireRabbitMq(rmq => rmq.Host("amqp://first@localhost:5672/"));

        // Act
        Action act = () => services.AddBareWireRabbitMq(rmq => rmq.Host("amqp://second@localhost:5672/"));

        // Assert
        act.Should().Throw<BareWireConfigurationException>().Which.Message.Should().Contain("already called");
        services.Where(d => d.ServiceType == typeof(RabbitMqTransportOptions)).Should().ContainSingle();
    }

    [Fact]
    public void AddBareWireRabbitMq_NullServices_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => BareWire.Transport.RabbitMQ.ServiceCollectionExtensions.AddBareWireRabbitMq(null!, _ => { });

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("services");
    }

    [Fact]
    public void AddBareWireRabbitMq_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        Action act = () => services.AddBareWireRabbitMq(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("configure");
    }

    [Fact]
    public void AddBareWireRabbitMq_QueueWithDlx_SetsHasDeadLetterExchange()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddBareWireRabbitMq(rmq =>
        {
            rmq.Host("amqp://guest:guest@localhost:5672/");
            rmq.ConfigureTopology(t =>
            {
                t.DeclareExchange("events", ExchangeType.Fanout, durable: true);
                t.DeclareQueue("orders", durable: true, arguments:
                    new Dictionary<string, object> { ["x-dead-letter-exchange"] = "orders.dlx" });
                t.BindExchangeToQueue("events", "orders", routingKey: "#");
            });
            rmq.ReceiveEndpoint("orders", e =>
            {
                e.Consumer<DummyConsumer, DummyMessage>();
            });
        });

        // Assert
        using var provider = services.BuildServiceProvider();
        var bindings = provider.GetRequiredService<IReadOnlyList<EndpointBinding>>();
        bindings.Should().ContainSingle()
            .Which.HasDeadLetterExchange.Should().BeTrue();
    }

    [Fact]
    public void AddBareWireRabbitMq_QueueWithoutDlx_HasDeadLetterExchangeIsFalse()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddBareWireRabbitMq(rmq =>
        {
            rmq.Host("amqp://guest:guest@localhost:5672/");
            rmq.ConfigureTopology(t =>
            {
                t.DeclareExchange("events", ExchangeType.Fanout, durable: true);
                t.DeclareQueue("orders", durable: true);
                t.BindExchangeToQueue("events", "orders", routingKey: "#");
            });
            rmq.ReceiveEndpoint("orders", e =>
            {
                e.Consumer<DummyConsumer, DummyMessage>();
            });
        });

        // Assert
        using var provider = services.BuildServiceProvider();
        var bindings = provider.GetRequiredService<IReadOnlyList<EndpointBinding>>();
        bindings.Should().ContainSingle()
            .Which.HasDeadLetterExchange.Should().BeFalse();
    }

    [Fact]
    public void AddBareWireRabbitMq_NoTopologyConfigured_HasDeadLetterExchangeIsFalse()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddBareWireRabbitMq(rmq =>
        {
            rmq.Host("amqp://guest:guest@localhost:5672/");
            rmq.ReceiveEndpoint("orders", e =>
            {
                e.Consumer<DummyConsumer, DummyMessage>();
            });
        });

        // Assert
        using var provider = services.BuildServiceProvider();
        var bindings = provider.GetRequiredService<IReadOnlyList<EndpointBinding>>();
        bindings.Should().ContainSingle()
            .Which.HasDeadLetterExchange.Should().BeFalse();
    }

    private sealed record DummyMessage(string Id);

    private sealed class DummyConsumer : BareWire.Abstractions.IConsumer<DummyMessage>
    {
        public Task ConsumeAsync(BareWire.Abstractions.ConsumeContext<DummyMessage> context)
            => Task.CompletedTask;
    }
}
