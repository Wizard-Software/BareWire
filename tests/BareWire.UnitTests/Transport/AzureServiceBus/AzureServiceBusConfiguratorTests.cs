using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using Xunit;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

public sealed class AzureServiceBusConfiguratorTests
{
    private const string ValidConnectionString =
        "Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=secret==";

    // Helper: configurator with a valid connection string already set.
    private static AzureServiceBusConfigurator WithConnectionString(string cs = ValidConnectionString)
    {
        var c = new AzureServiceBusConfigurator();
        c.ConnectionString(cs);
        return c;
    }

    // ── ConnectionString ──────────────────────────────────────────────────────

    [Fact]
    public void Build_WithConnectionString_SetsConnectionString()
    {
        // Arrange
        AzureServiceBusConfigurator configurator = WithConnectionString();

        // Act
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert
        options.ConnectionString.Should().Be(ValidConnectionString);
    }

    [Fact]
    public void ConnectionString_EmptyValue_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new AzureServiceBusConfigurator();

        // Act
        Action act = () => configurator.ConnectionString(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ConnectionString_NullValue_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new AzureServiceBusConfigurator();

        // Act
        Action act = () => configurator.ConnectionString(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── Build_WithoutConnectionString ─────────────────────────────────────────

    [Fact]
    public void Build_WithoutConnectionString_Throws()
    {
        // Arrange — no ConnectionString call
        var configurator = new AzureServiceBusConfigurator();

        // Act
        Action act = () => configurator.Build();

        // Assert — Validate() is called from Build()
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.ConnectionString));
    }

    // ── PrefetchCount ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_SetsPrefetchCount()
    {
        // Arrange
        AzureServiceBusConfigurator configurator = WithConnectionString();
        configurator.PrefetchCount(50);

        // Act
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert
        options.PrefetchCount.Should().Be(50);
    }

    [Fact]
    public void Build_WithoutPrefetchCount_DefaultsToZero()
    {
        // Arrange — no PrefetchCount call
        AzureServiceBusConfigurator configurator = WithConnectionString();

        // Act
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert
        options.PrefetchCount.Should().Be(0);
    }

    // ── MaxConcurrentCalls ────────────────────────────────────────────────────

    [Fact]
    public void Build_SetsMaxConcurrentCalls()
    {
        // Arrange
        AzureServiceBusConfigurator configurator = WithConnectionString();
        configurator.MaxConcurrentCalls(4);

        // Act
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert
        options.MaxConcurrentCalls.Should().Be(4);
    }

    [Fact]
    public void Build_WithoutMaxConcurrentCalls_DefaultsToOne()
    {
        // Arrange — no MaxConcurrentCalls call
        AzureServiceBusConfigurator configurator = WithConnectionString();

        // Act
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert
        options.MaxConcurrentCalls.Should().Be(1);
    }
}
