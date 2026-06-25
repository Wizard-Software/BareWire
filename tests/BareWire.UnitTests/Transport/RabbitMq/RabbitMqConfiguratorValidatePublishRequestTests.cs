using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqConfiguratorValidatePublishRequestTests
{
    private sealed record PaymentRequested(decimal Amount);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    // ── T1 (KA a): PublishRequest without topology AND empty _exchangeMappings → throws ───────────
    // Proves ValidateExchangeMappings (only run when _exchangeMappings.Count > 0) does NOT cover the
    // publish-request map: _exchangeMappings is empty here, yet Build() still throws.

    [Fact]
    public void Build_WhenPublishRequestWithoutTopologyAndEmptyExchangeMappings_ThrowsConfigurationException()
    {
        // Arrange — PublishRequest<T> (bare → formatter), no ConfigureTopology, no MapExchange.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>();

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("PublishRequest");
    }

    // ── T2 (KA a): exchange not present in topology → throws with OptionValue == exchange name ────

    [Fact]
    public void Build_WhenPublishRequestExchangeNotInTopology_ThrowsConfigurationException()
    {
        // Arrange — topology declares a different fanout exchange; the per-type exchange is absent.
        string expected = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("other.fanout", ExchangeType.Fanout));
        configurator.PublishRequest<PaymentRequested>();

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("PublishRequest");
        ex.OptionValue.Should().Be(expected);
    }

    // ── T3 (KA b): declared exchange is Direct → throws (no silent broadcast loss) ────────────────

    [Fact]
    public void Build_WhenDeclaredExchangeIsDirect_ThrowsConfigurationException()
    {
        // Arrange — an exchange of the per-type name exists but is Direct, not Fanout.
        string exchange = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange(exchange, ExchangeType.Direct));
        configurator.PublishRequest<PaymentRequested>();

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("PublishRequest");
        ex.OptionValue.Should().Be(exchange);
    }

    // ── T4 (KA b): declared exchange is Topic → throws ───────────────────────────────────────────

    [Fact]
    public void Build_WhenDeclaredExchangeIsTopic_ThrowsConfigurationException()
    {
        // Arrange — an exchange of the per-type name exists but is Topic, not Fanout.
        string exchange = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange(exchange, ExchangeType.Topic));
        configurator.PublishRequest<PaymentRequested>();

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("PublishRequest");
        ex.OptionValue.Should().Be(exchange);
    }

    // ── T5 (KA c): OptionValue is the exchange name (SEC S1 — never correlation-id/body) ─────────

    [Fact]
    public void Build_WhenValidationFails_OptionValueIsExchangeName()
    {
        // Arrange — explicit override name that is not declared anywhere.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("declared.fanout", ExchangeType.Fanout));
        configurator.PublishRequest<PaymentRequested>(o => o.ExchangeName = "custom.fanout");

        // Act
        Action act = () => configurator.Build();

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionValue.Should().Be("custom.fanout");
    }

    // ── T6 (happy): exchange declared as Fanout → Build succeeds ──────────────────────────────────

    [Fact]
    public void Build_WhenExchangeDeclaredAsFanout_Succeeds()
    {
        // Arrange — matching Fanout exchange declared in topology.
        string exchange = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange(exchange, ExchangeType.Fanout));
        configurator.PublishRequest<PaymentRequested>();

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRequestMappings.Should().NotBeNull();
        options.PublishRequestMappings![typeof(PaymentRequested)].ExchangeName.Should().Be(exchange);
    }

    // ── T7 (AutoDeclare): AutoDeclare = true skips validation even without topology ───────────────

    [Fact]
    public void Build_WhenAutoDeclareTrue_SkipsValidationEvenWithoutTopology()
    {
        // Arrange — AutoDeclare = true, no ConfigureTopology.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o => o.AutoDeclare = true);

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRequestMappings.Should().NotBeNull();
        options.PublishRequestMappings!.Should().ContainKey(typeof(PaymentRequested));
    }
}
