using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Configuration;

namespace BareWire.UnitTests.Core.Configuration;

public sealed class ConfigurationValidatorTests
{
    // ── Helper factories ───────────────────────────────────────────────────────

    private static BusConfigurator CreateValidConfigurator()
    {
        BusConfigurator configurator = new();
        configurator.HasInMemoryTransport = true;
        return configurator;
    }

    private static BusConfigurator CreateValidConfiguratorWithEndpoint(
        string endpointName = "test-queue",
        Action<IReceiveEndpointConfigurator>? configure = null)
    {
        BusConfigurator configurator = CreateValidConfigurator();
        configurator.AddReceiveEndpoint(endpointName, ep =>
        {
            // Default valid endpoint: one consumer.
            ep.Consumer<FakeConsumer, FakeMessage>();
            configure?.Invoke(ep);
        });
        return configurator;
    }

    // ── Transport validation ───────────────────────────────────────────────────

    [Fact]
    public void Validate_NoTransport_ThrowsConfigurationException()
    {
        // Arrange — configurator is otherwise valid but transportRegistered is false,
        // meaning no ITransportAdapter was resolved into the control (D5 / ADR-028).
        BusConfigurator configurator = new();

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: false);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be("Transport");
    }

    [Fact]
    public void Validate_WithInMemoryTransport_DoesNotThrowForTransport()
    {
        // Arrange
        BusConfigurator configurator = new();
        configurator.HasInMemoryTransport = true;

        // Act — no endpoints so no endpoint-level errors, only checking transport passes
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert — transport check passes; no exception thrown (no endpoints to validate)
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithRabbitMQTransport_DoesNotThrowForTransport()
    {
        // Arrange
        BusConfigurator configurator = new();
        configurator.UseRabbitMQ(_ => { }); // Sets RabbitMqConfigurator

        // Act — no endpoints, only transport check
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert — transport check passes
        act.Should().NotThrow();
    }

    // ── transportRegistered branch tests (core of task 15.2) ──────────────────

    [Fact]
    public void Validate_TransportNotRegistered_ThrowsConfigurationException()
    {
        // Arrange — any configurator; transportRegistered: false simulates the absence of
        // ITransportAdapter in the DI container (D5 / ADR-028).
        BusConfigurator configurator = new();

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: false);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be("Transport");
    }

    [Fact]
    public void Validate_TransportRegistered_DoesNotThrowForTransport()
    {
        // Arrange — BusConfigurator with NO HasTransport marker set (neither HasInMemoryTransport
        // nor UseRabbitMQ called). This is the key proof that HasTransport is no longer load-bearing:
        // when transportRegistered is true, the validator must not throw even without the marker.
        BusConfigurator configurator = new();

        // Act — no endpoints either, so only transport validation is exercised
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert
        act.Should().NotThrow();
    }

    // ── Endpoint consumer validation ───────────────────────────────────────────

    [Fact]
    public void Validate_EndpointWithoutConsumer_ThrowsConfigurationException()
    {
        // Arrange
        BusConfigurator configurator = CreateValidConfigurator();
        configurator.AddReceiveEndpoint("empty-queue", ep =>
        {
            // No consumer registered on purpose.
            ep.PrefetchCount = 16;
        });

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Contain("empty-queue");
    }

    [Fact]
    public void Validate_EndpointWithRawConsumer_DoesNotThrow()
    {
        // Arrange
        BusConfigurator configurator = CreateValidConfigurator();
        configurator.AddReceiveEndpoint("raw-queue", ep =>
        {
            ep.RawConsumer<FakeRawConsumer>();
        });

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert
        act.Should().NotThrow();
    }

    // ── PrefetchCount validation ───────────────────────────────────────────────

    [Fact]
    public void Validate_PrefetchCountZero_ThrowsConfigurationException()
    {
        // Arrange
        BusConfigurator configurator = CreateValidConfigurator();
        configurator.AddReceiveEndpoint("test-queue", ep =>
        {
            ep.Consumer<FakeConsumer, FakeMessage>();
            ep.PrefetchCount = 0;
        });

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Contain("PrefetchCount");
        ex.OptionValue.Should().Be("0");
    }

    [Fact]
    public void Validate_PrefetchCountNegative_ThrowsConfigurationException()
    {
        // Arrange
        BusConfigurator configurator = CreateValidConfigurator();
        configurator.AddReceiveEndpoint("test-queue", ep =>
        {
            ep.Consumer<FakeConsumer, FakeMessage>();
            ep.PrefetchCount = -1;
        });

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Contain("PrefetchCount");
    }

    // ── ConcurrentMessageLimit validation ─────────────────────────────────────

    [Fact]
    public void Validate_ConcurrentMessageLimitZero_ThrowsConfigurationException()
    {
        // Arrange
        BusConfigurator configurator = CreateValidConfigurator();
        configurator.AddReceiveEndpoint("test-queue", ep =>
        {
            ep.Consumer<FakeConsumer, FakeMessage>();
            ep.ConcurrentMessageLimit = 0;
        });

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Contain("ConcurrentMessageLimit");
        ex.OptionValue.Should().Be("0");
    }

    // ── Valid configuration ────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        // Arrange — transport set, one endpoint with a consumer and valid numeric options.
        BusConfigurator configurator = CreateValidConfiguratorWithEndpoint();

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MultipleValidEndpoints_DoesNotThrow()
    {
        // Arrange
        BusConfigurator configurator = CreateValidConfigurator();
        configurator.AddReceiveEndpoint("queue-a", ep => ep.Consumer<FakeConsumer, FakeMessage>());
        configurator.AddReceiveEndpoint("queue-b", ep => ep.RawConsumer<FakeRawConsumer>());

        // Act
        Action act = () => ConfigurationValidator.Validate(configurator, transportRegistered: true);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_NullConfigurator_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => ConfigurationValidator.Validate(null!, transportRegistered: true);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Fake types for use in generic consumer registration ───────────────────

    private sealed record FakeMessage;

    private sealed class FakeConsumer : IConsumer<FakeMessage>
    {
        public Task ConsumeAsync(ConsumeContext<FakeMessage> context) => Task.CompletedTask;
    }

    private sealed class FakeRawConsumer : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context) => Task.CompletedTask;
    }
}
