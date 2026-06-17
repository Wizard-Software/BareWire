using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.AzureServiceBus;
using Xunit;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

public sealed class AzureServiceBusTransportOptionsTests
{
    // ── Validate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenConnectionStringEmpty_ThrowsConfigurationException()
    {
        // Arrange
        var options = new AzureServiceBusTransportOptions { ConnectionString = string.Empty };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.ConnectionString));
    }

    [Fact]
    public void Validate_WhenConnectionStringNull_ThrowsConfigurationException()
    {
        // Arrange
        var options = new AzureServiceBusTransportOptions { ConnectionString = null! };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(AzureServiceBusTransportOptions.ConnectionString));
    }

    [Fact]
    public void Validate_WhenConnectionStringSet_DoesNotThrow()
    {
        // Arrange
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = "Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=secret"
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().NotThrow();
    }

    // ── Validate — exception does not echo the secret ────────────────────────

    [Fact]
    public void Validate_WhenConnectionStringEmpty_ExceptionMessageDoesNotContainValue()
    {
        // SEC-02: the exception message must not echo the (empty) connection string value
        // back in a way that could leak context about the expected format.
        var options = new AzureServiceBusTransportOptions { ConnectionString = string.Empty };

        BareWireConfigurationException ex =
            Record.Exception(() => options.Validate()) as BareWireConfigurationException
            ?? throw new InvalidOperationException("Expected BareWireConfigurationException.");

        // The OptionValue passed to the exception ctor must be empty — never the raw connection string.
        ex.OptionValue.Should().Be(string.Empty);
    }

    // ── ToString redaction (SEC-02 / SEC-06) ─────────────────────────────────

    [Fact]
    public void ToString_DoesNotContainConnectionString()
    {
        // Arrange — use a sentinel value that a spoofed log would contain verbatim.
        const string sentinel = "SharedAccessKey=secret";
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = $"Endpoint=sb://myns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;{sentinel}",
            PrefetchCount = 10,
            MaxConcurrentCalls = 2,
        };

        // Act
        string rendered = options.ToString();

        // Assert — SEC-02/SEC-06: the secret sentinel MUST NOT appear in the rendered string.
        rendered.Should().NotContain(sentinel,
            "the connection string (which contains a SharedAccessKey) must be redacted in ToString()");

        // And the redaction placeholder MUST be present.
        rendered.Should().Contain("[Redacted]");
    }

    [Fact]
    public void ToString_ContainsNonSecretProperties()
    {
        // Arrange
        var options = new AzureServiceBusTransportOptions
        {
            ConnectionString = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=s",
            PrefetchCount = 5,
            MaxConcurrentCalls = 3,
        };

        // Act
        string rendered = options.ToString();

        // Assert — non-secret properties are visible for diagnostics.
        rendered.Should().Contain("PrefetchCount = 5");
        rendered.Should().Contain("MaxConcurrentCalls = 3");
    }

    // ── Default values ────────────────────────────────────────────────────────

    [Fact]
    public void PrefetchCount_DefaultsToZero()
    {
        var options = new AzureServiceBusTransportOptions();
        options.PrefetchCount.Should().Be(0);
    }

    [Fact]
    public void MaxConcurrentCalls_DefaultsToOne()
    {
        var options = new AzureServiceBusTransportOptions();
        options.MaxConcurrentCalls.Should().Be(1);
    }
}
