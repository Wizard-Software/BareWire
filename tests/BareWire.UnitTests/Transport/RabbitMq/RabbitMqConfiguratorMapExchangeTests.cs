using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqConfiguratorMapExchangeTests
{
    private sealed record PaymentRequested(decimal Amount);
    private sealed record OrderCreated(string OrderId);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    // ── MapExchange_NullGuard ─────────────────────────────────────────────────

    [Fact]
    public void MapExchange_WithNullExchangeName_ThrowsArgumentException()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();

        // Act
        Action act = () => configurator.MapExchange<PaymentRequested>(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── MapExchange_EmptyGuard ────────────────────────────────────────────────

    [Fact]
    public void MapExchange_WithEmptyExchangeName_ThrowsArgumentException()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();

        // Act
        Action act = () => configurator.MapExchange<PaymentRequested>(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── MapExchange_DuplicateType_LastWins ────────────────────────────────────

    [Fact]
    public void MapExchange_WithDuplicateTypeCall_LastWins()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("payments.topic", ExchangeType.Topic);
            t.DeclareExchange("orders.topic", ExchangeType.Topic);
        });

        configurator.MapExchange<PaymentRequested>("payments.topic");
        configurator.MapExchange<PaymentRequested>("orders.topic"); // second call wins

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.ExchangeMappings.Should().NotBeNull();
        options.ExchangeMappings![typeof(PaymentRequested)].Should().Be("orders.topic");
    }

    // ── Build_TopologyNotDeclared ─────────────────────────────────────────────

    [Fact]
    public void Build_WhenExchangeMappedButTopologyNotDeclared_ThrowsConfigurationException()
    {
        // Arrange — no ConfigureTopology call
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.MapExchange<PaymentRequested>("payments.topic");

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("MapExchange");
    }

    // ── Build_ExchangeNotInTopology ───────────────────────────────────────────

    [Fact]
    public void Build_WhenExchangeMappedButExchangeNotInTopology_ThrowsConfigurationException()
    {
        // Arrange — topology declared but exchange "payments.topic" is not in it
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("orders.direct", ExchangeType.Direct));
        configurator.MapExchange<PaymentRequested>("payments.topic");

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("MapExchange");
        ex.OptionValue.Should().Contain("payments.topic");
    }

    // ── Build_MultipleExchangesMissing_ReportsAll ─────────────────────────────

    [Fact]
    public void Build_WhenMultipleExchangesMissingInTopology_ReportsAllInSingleException()
    {
        // Arrange — topology has no exchanges, but two mappings reference missing exchanges
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("only.declared", ExchangeType.Topic));
        configurator.MapExchange<PaymentRequested>("payments.topic");
        configurator.MapExchange<OrderCreated>("orders.fanout");

        // Act
        Action act = () => configurator.Build();

        // Assert — single exception, containing both missing names
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("MapExchange");
        ex.OptionValue.Should().Contain("payments.topic");
        ex.OptionValue.Should().Contain("orders.fanout");
    }

    // ── Build_HappyPath ───────────────────────────────────────────────────────

    [Fact]
    public void Build_WhenMappingValid_PopulatesOptionsExchangeMappings()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("payments.topic", ExchangeType.Topic);
            t.DeclareExchange("orders.fanout", ExchangeType.Fanout);
        });
        configurator.MapExchange<PaymentRequested>("payments.topic");
        configurator.MapExchange<OrderCreated>("orders.fanout");

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.ExchangeMappings.Should().NotBeNull();
        options.ExchangeMappings![typeof(PaymentRequested)].Should().Be("payments.topic");
        options.ExchangeMappings![typeof(OrderCreated)].Should().Be("orders.fanout");
    }
}
