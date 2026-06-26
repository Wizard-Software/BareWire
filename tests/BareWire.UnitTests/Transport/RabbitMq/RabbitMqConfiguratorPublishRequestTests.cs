using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqConfiguratorPublishRequestTests
{
    private sealed record PaymentRequested(decimal Amount);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    // ── T1: last-call-wins ────────────────────────────────────────────────────

    [Fact]
    public void PublishRequest_CalledTwiceForSameType_LastWins()
    {
        // Arrange — AutoDeclare = true skips topology validation (this test targets last-call-wins,
        // not the ValidatePublishRequestMappings fail-fast path).
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o =>
        {
            o.ExchangeName = "first";
            o.AutoDeclare = true;
        });
        configurator.PublishRequest<PaymentRequested>(o =>
        {
            o.ExchangeName = "second";
            o.AutoDeclare = true;
        });

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRequestMappings.Should().NotBeNull();
        options.PublishRequestMappings![typeof(PaymentRequested)].ExchangeName.Should().Be("second");
    }

    // ── T2: ExchangeName override stored ─────────────────────────────────────

    [Fact]
    public void PublishRequest_WithExchangeNameOverride_StoresOverride()
    {
        // Arrange — AutoDeclare = true skips topology validation (this test targets the ExchangeName
        // override being stored, not the ValidatePublishRequestMappings fail-fast path).
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o =>
        {
            o.ExchangeName = "custom.fanout";
            o.AutoDeclare = true;
        });

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRequestMappings.Should().NotBeNull();
        options.PublishRequestMappings![typeof(PaymentRequested)].ExchangeName.Should().Be("custom.fanout");
    }

    // ── T3: bare overload stores formatter name ───────────────────────────────

    [Fact]
    public void PublishRequest_WithoutExchangeName_StoresFormatterName()
    {
        // Arrange — declare the matching Fanout exchange so the bare overload (AutoDeclare = false)
        // passes ValidatePublishRequestMappings; this test targets the stored formatter name.
        string expected = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange(expected, ExchangeType.Fanout));
        configurator.PublishRequest<PaymentRequested>();

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRequestMappings.Should().NotBeNull();
        options.PublishRequestMappings![typeof(PaymentRequested)].ExchangeName.Should().Be(expected);
    }

    // ── T4: bare overload defaults flags to false ─────────────────────────────

    [Fact]
    public void PublishRequest_BareOverload_DefaultsFlagsFalse()
    {
        // Arrange — declare the matching Fanout exchange so the bare overload (AutoDeclare = false)
        // passes ValidatePublishRequestMappings; this test targets the default flag values.
        string exchange = RequestExchangeNameFormatter.Format<PaymentRequested>();
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange(exchange, ExchangeType.Fanout));
        configurator.PublishRequest<PaymentRequested>();

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRequestMappings.Should().NotBeNull();
        PublishRequestRegistration reg = options.PublishRequestMappings![typeof(PaymentRequested)];
        reg.Strict.Should().BeFalse();
        reg.AutoDeclare.Should().BeFalse();
    }

    // ── T5: Strict and AutoDeclare propagated ─────────────────────────────────

    [Fact]
    public void PublishRequest_WithStrictAndAutoDeclare_PropagatesFlags()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.PublishRequest<PaymentRequested>(o =>
        {
            o.Strict = true;
            o.AutoDeclare = true;
        });

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRequestMappings.Should().NotBeNull();
        PublishRequestRegistration reg = options.PublishRequestMappings![typeof(PaymentRequested)];
        reg.Strict.Should().BeTrue();
        reg.AutoDeclare.Should().BeTrue();
    }

    // ── T6: null configure throws ArgumentNullException ───────────────────────

    [Fact]
    public void PublishRequest_ConfigureOverload_WithNullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();

        // Act
        Action act = () => configurator.PublishRequest<PaymentRequested>(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── T7: no PublishRequest call leaves mappings null ───────────────────────

    [Fact]
    public void Build_WithoutAnyPublishRequest_LeavesMappingsNull()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRequestMappings.Should().BeNull();
    }
}
