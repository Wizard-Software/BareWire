using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqConfiguratorTests
{
    // ── Host_ValidUri ─────────────────────────────────────────────────────────

    [Fact]
    public void Host_ValidAmqpUri_SetsConnectionString()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        configurator.Host("amqp://guest:guest@localhost:5672/");
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.ConnectionString.Should().Be("amqp://guest:guest@localhost:5672/");
    }

    [Fact]
    public void Host_ValidAmqpsUri_SetsConnectionString()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        configurator.Host("amqps://broker.example.com:5671/");
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.ConnectionString.Should().Be("amqps://broker.example.com:5671/");
    }

    // ── Host_EmptyUri ─────────────────────────────────────────────────────────

    [Fact]
    public void Host_EmptyUri_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        Action act = () => configurator.Host(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Host_NullUri_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        Action act = () => configurator.Host(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── Host_InvalidFormat ────────────────────────────────────────────────────

    [Fact]
    public void Host_InvalidFormat_ThrowsConfigurationException()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        configurator.Host("not-a-uri");

        // Act — Build() validates the URI format
        Action act = () => configurator.Build();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be("Host");
    }

    [Fact]
    public void Host_HttpScheme_ThrowsConfigurationException()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        configurator.Host("http://localhost:5672/");

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("Host");
        ex.OptionValue.Should().Be("http://localhost:5672/");
    }

    // ── Host_WithCredentials ──────────────────────────────────────────────────

    [Fact]
    public void Host_WithCredentials_SetsUsernamePassword()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        configurator.Host("amqp://localhost:5672/", h =>
        {
            h.Username("myuser");
            h.Password("mypass");
        });
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.UsernameOverride.Should().Be("myuser");
        options.PasswordOverride.Should().Be("mypass");
    }

    [Fact]
    public void Host_WithUsernameOnly_SetsUsernameOverride()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        configurator.Host("amqp://localhost:5672/", h => h.Username("admin"));
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.UsernameOverride.Should().Be("admin");
        options.PasswordOverride.Should().BeNull();
    }

    // ── Host_WithTls ──────────────────────────────────────────────────────────

    [Fact]
    public void Host_WithTls_SetsTlsConfigureCallback()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        var tlsCallbackInvoked = false;

        // Act
        configurator.Host("amqps://broker.example.com:5671/", h =>
        {
            h.UseTls(tls =>
            {
                tlsCallbackInvoked = true;
                tls.WithCertificate("/certs/client.pfx");
            });
        });
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — ConfigureTls delegate is stored; invoke it to verify
        options.ConfigureTls.Should().NotBeNull();
        var tlsConfigurator = new RabbitMqTlsConfigurator();
        options.ConfigureTls(tlsConfigurator);
        tlsCallbackInvoked.Should().BeTrue();
    }

    // ── ReceiveEndpoint_MultipleEndpoints ─────────────────────────────────────

    [Fact]
    public void ReceiveEndpoint_MultipleEndpoints_AccumulatesAll()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://localhost:5672/");

        // Act
        configurator.ReceiveEndpoint("queue-a", ep => ep.PrefetchCount = 8);
        configurator.ReceiveEndpoint("queue-b", ep => ep.PrefetchCount = 16);
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.EndpointConfigurations.Should().HaveCount(2);
        options.EndpointConfigurations[0].QueueName.Should().Be("queue-a");
        options.EndpointConfigurations[0].PrefetchCount.Should().Be(8);
        options.EndpointConfigurations[1].QueueName.Should().Be("queue-b");
        options.EndpointConfigurations[1].PrefetchCount.Should().Be(16);
    }

    [Fact]
    public void ReceiveEndpoint_NullQueueName_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        Action act = () => configurator.ReceiveEndpoint(null!, _ => { });

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReceiveEndpoint_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        Action act = () => configurator.ReceiveEndpoint("queue-a", null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ConfigureTopology ─────────────────────────────────────────────────────

    [Fact]
    public void ConfigureTopology_DelegatesToTopologyConfigurator()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://localhost:5672/");

        // Act
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("orders", ExchangeType.Direct);
            t.DeclareQueue("orders-queue");
            t.BindExchangeToQueue("orders", "orders-queue", "order.created");
        });
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.Topology.Should().NotBeNull();
        options.Topology!.Exchanges.Should().HaveCount(1);
        options.Topology.Queues.Should().HaveCount(1);
        options.Topology.ExchangeQueueBindings.Should().HaveCount(1);
    }

    [Fact]
    public void ConfigureTopology_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        Action act = () => configurator.ConfigureTopology(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConfigureTopology_CalledTwice_AccumulatesDeclarations()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://localhost:5672/");

        // Act — second call should accumulate on the same topology configurator
        configurator.ConfigureTopology(t => t.DeclareExchange("ex-a", ExchangeType.Direct));
        configurator.ConfigureTopology(t => t.DeclareExchange("ex-b", ExchangeType.Fanout));
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.Topology!.Exchanges.Should().HaveCount(2);
    }

    // ── ConfigureHeaderMapping ────────────────────────────────────────────────

    [Fact]
    public void ConfigureHeaderMapping_DelegatesToHeaderConfigurator()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://localhost:5672/");

        // Act
        configurator.ConfigureHeaderMapping(h =>
        {
            h.MapCorrelationId("X-Correlation-ID");
            h.MapMessageType("X-Message-Type");
        });

        // Assert — no exception; mapping was invoked successfully; build completes
        Action act = () => configurator.Build();
        act.Should().NotThrow();
    }

    [Fact]
    public void ConfigureHeaderMapping_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();

        // Act
        Action act = () => configurator.ConfigureHeaderMapping(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Build_NoHost ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_NoHost_ThrowsConfigurationException()
    {
        // Arrange — no Host() call before Build()
        var configurator = new RabbitMqConfigurator();

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("Host");
    }

    // ── Build_NoTopology ──────────────────────────────────────────────────────

    [Fact]
    public void Build_NoTopology_TopologyIsNull()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://localhost:5672/");

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — topology is optional
        options.Topology.Should().BeNull();
    }

    // ── Build_NoEndpoints ─────────────────────────────────────────────────────

    [Fact]
    public void Build_NoEndpoints_ReturnsEmptyEndpointList()
    {
        // Arrange
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://localhost:5672/");

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.EndpointConfigurations.Should().BeEmpty();
    }
}
