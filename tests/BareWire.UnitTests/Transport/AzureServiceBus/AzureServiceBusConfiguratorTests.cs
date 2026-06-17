using Azure.Core;
using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using NSubstitute;
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

    // Helper: creates a substitute TokenCredential (NSubstitute, no real token exchange).
    private static TokenCredential FakeCredential() => Substitute.For<TokenCredential>();

    // ── UseSasAuth ────────────────────────────────────────────────────────────

    [Fact]
    public void UseSasAuth_SetsConnectionStringAndSasMode()
    {
        // Arrange
        var configurator = new AzureServiceBusConfigurator();

        // Act
        configurator.UseSasAuth(ValidConnectionString);
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert
        options.AuthMode.Should().Be(AzureServiceBusAuthMode.Sas);
        options.ConnectionString.Should().Be(ValidConnectionString);
    }

    // ── UseEntraIdAuth ────────────────────────────────────────────────────────

    [Fact]
    public void UseEntraIdAuth_SetsNamespaceCredentialAndEntraMode()
    {
        // Arrange
        const string ns = "myns.servicebus.windows.net";
        TokenCredential cred = FakeCredential();
        var configurator = new AzureServiceBusConfigurator();

        // Act
        configurator.UseEntraIdAuth(ns, cred);
        AzureServiceBusTransportOptions options = configurator.Build();

        // Assert
        options.AuthMode.Should().Be(AzureServiceBusAuthMode.EntraId);
        options.FullyQualifiedNamespace.Should().Be(ns);
        options.Credential.Should().BeSameAs(cred);
    }

    [Fact]
    public void UseEntraIdAuth_NullOrEmptyNamespace_ThrowsArgumentException()
    {
        // Arrange
        TokenCredential cred = FakeCredential();
        var configurator = new AzureServiceBusConfigurator();

        // Act — null namespace
        Action actNull = () => configurator.UseEntraIdAuth(null!, cred);
        Action actEmpty = () => configurator.UseEntraIdAuth(string.Empty, cred);

        // Assert
        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UseEntraIdAuth_NullCredential_ThrowsArgumentNullException()
    {
        // Arrange
        var configurator = new AzureServiceBusConfigurator();

        // Act
        Action act = () => configurator.UseEntraIdAuth("myns.servicebus.windows.net", null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Build — EntraId validation ────────────────────────────────────────────

    [Fact]
    public void Build_EntraIdWithoutNamespace_Throws()
    {
        // Arrange — credential set but no namespace
        var configurator = new AzureServiceBusConfigurator();
        // Manually set EntraId mode by using UseEntraIdAuth then clearing namespace isn't possible
        // so we test Validate() directly with options
        var options = new AzureServiceBusTransportOptions
        {
            AuthMode = AzureServiceBusAuthMode.EntraId,
            FullyQualifiedNamespace = string.Empty,
            Credential = FakeCredential(),
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.FullyQualifiedNamespace));
    }

    [Fact]
    public void Build_EntraIdWithoutCredential_Throws()
    {
        // Arrange — namespace set but no credential
        var options = new AzureServiceBusTransportOptions
        {
            AuthMode = AzureServiceBusAuthMode.EntraId,
            FullyQualifiedNamespace = "myns.servicebus.windows.net",
            Credential = null,
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.Credential));
    }

    // ── Build — SAS regression (R2.1 behaviour preserved) ─────────────────────

    [Fact]
    public void Build_SasWithoutConnectionString_Throws()
    {
        // Arrange — no ConnectionString call; default AuthMode is Sas
        var configurator = new AzureServiceBusConfigurator();

        // Act
        Action act = () => configurator.Build();

        // Assert — Validate() is called from Build(); SAS rule still enforced
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.ConnectionString));
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
