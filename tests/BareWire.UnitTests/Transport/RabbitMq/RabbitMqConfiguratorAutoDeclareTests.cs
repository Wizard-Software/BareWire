using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqConfiguratorAutoDeclareTests
{
    private sealed record PaymentRequested(decimal Amount);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    // ── (a) AutoDeclare=true, no ConfigureTopology call ──────────────────────

    [Fact]
    public void Build_WithAutoDeclare_WithoutConfigureTopology_DoesNotThrow()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o => o.AutoDeclare = true);

        // Act
        Action act = () => configurator.Build();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_WithAutoDeclare_WithoutConfigureTopology_TopologyIsNonNull()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o => o.AutoDeclare = true);

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.Topology.Should().NotBeNull();
    }

    [Fact]
    public void Build_WithAutoDeclare_WithoutConfigureTopology_AddsExchangeWithCorrectName()
    {
        // Arrange
        string expectedName = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o => o.AutoDeclare = true);

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.Topology!.Exchanges.Should().ContainSingle(e => e.Name == expectedName);
    }

    [Fact]
    public void Build_WithAutoDeclare_AddsExchangeAsFanout()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o => o.AutoDeclare = true);

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.Topology!.Exchanges[0].Type.Should().Be(ExchangeType.Fanout);
    }

    [Fact]
    public void Build_WithAutoDeclare_AddsExchangeAsDurable()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o => o.AutoDeclare = true);

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.Topology!.Exchanges[0].Durable.Should().BeTrue();
    }

    [Fact]
    public void Build_WithAutoDeclare_AddsExchangeAutoDeleteFalse()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o => o.AutoDeclare = true);

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.Topology!.Exchanges[0].AutoDelete.Should().BeFalse();
    }

    // ── (b) Default-OFF: bare overload (AutoDeclare=false) does NOT auto-declare ─

    [Fact]
    public void Build_BareOverload_WithExplicitTopology_ContainsExactlyOneExchange()
    {
        // Arrange — bare overload has AutoDeclare=false; user must declare the exchange manually.
        // This also proves auto-declare does not fire when the flag is off.
        string exchangeName = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange(exchangeName, ExchangeType.Fanout));
        configurator.PublishRequest<PaymentRequested>();

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — exactly one exchange (manually declared), auto-declare did not add a duplicate
        options.Topology!.Exchanges.Should().ContainSingle(e => e.Name == exchangeName);
    }

    [Fact]
    public void Build_BareOverload_WithoutTopology_Throws()
    {
        // Arrange — bare overload has AutoDeclare=false; without ConfigureTopology, Build() must fail.
        // This proves the merge did NOT silently create an exchange for a non-auto entry.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>();

        // Act
        Action act = () => configurator.Build();

        // Assert
        act.Should().Throw<BareWireConfigurationException>();
    }

    // ── (c) Idempotency: explicit declaration + AutoDeclare=true → no duplicate ─

    [Fact]
    public void Build_ExplicitDeclareAndAutoDeclare_ContainsExactlyOneExchange()
    {
        // Arrange — user declares the exchange via the Part A helper AND sets AutoDeclare=true.
        // The merge must not add a second copy.
        string expectedName = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareRequestExchange<PaymentRequested>());
        configurator.PublishRequest<PaymentRequested>(o => o.AutoDeclare = true);

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — exactly one exchange; no duplicate from the AutoDeclare merge
        options.Topology!.Exchanges
            .Count(e => e.Name == expectedName)
            .Should().Be(1);
    }
}
