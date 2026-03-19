using System.Net.Security;
using AwesomeAssertions;
using BareWire.Transport.RabbitMQ.Configuration;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqHostConfiguratorTests
{
    // ── Username ──────────────────────────────────────────────────────────────

    [Fact]
    public void Username_SetsValue()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();

        // Act
        configurator.Username("admin");

        // Assert
        configurator.UsernameValue.Should().Be("admin");
    }

    [Fact]
    public void Username_NullValue_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();

        // Act
        Action act = () => configurator.Username(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Username_EmptyValue_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();

        // Act
        Action act = () => configurator.Username(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── Password ──────────────────────────────────────────────────────────────

    [Fact]
    public void Password_SetsValue()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();

        // Act
        configurator.Password("s3cr3t");

        // Assert
        configurator.PasswordValue.Should().Be("s3cr3t");
    }

    [Fact]
    public void Password_NullValue_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();

        // Act
        Action act = () => configurator.Password(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Password_EmptyValue_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();

        // Act
        Action act = () => configurator.Password(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── UseTls ────────────────────────────────────────────────────────────────

    [Fact]
    public void UseTls_StoresTlsConfigureCallback()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();
        var callbackInvoked = false;

        // Act
        configurator.UseTls(tls =>
        {
            callbackInvoked = true;
        });

        // Assert — callback is stored
        configurator.TlsConfigure.Should().NotBeNull();

        // Invoke to verify the correct callback was stored
        var tlsConfigurator = new RabbitMqTlsConfigurator();
        configurator.TlsConfigure!(tlsConfigurator);
        callbackInvoked.Should().BeTrue();
    }

    [Fact]
    public void UseTls_DelegatesToTlsConfigurator()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();

        // Act
        configurator.UseTls(tls =>
        {
            tls.WithCertificate("/certs/client.pfx", "pass");
            tls.WithServerValidation(SslPolicyErrors.RemoteCertificateNameMismatch);
        });

        // Assert — invoke the stored callback and verify TLS settings are applied
        var tlsConfigurator = new RabbitMqTlsConfigurator();
        configurator.TlsConfigure!(tlsConfigurator);
        var sslOption = tlsConfigurator.Build("broker.example.com");

        sslOption.Enabled.Should().BeTrue();
        sslOption.CertPath.Should().Be("/certs/client.pfx");
        sslOption.CertPassphrase.Should().Be("pass");
        sslOption.AcceptablePolicyErrors.Should().Be(SslPolicyErrors.RemoteCertificateNameMismatch);
    }

    [Fact]
    public void UseTls_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        var configurator = new RabbitMqHostConfigurator();

        // Act
        Action act = () => configurator.UseTls(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Default state ─────────────────────────────────────────────────────────

    [Fact]
    public void DefaultState_AllValuesAreNull()
    {
        // Arrange & Act
        var configurator = new RabbitMqHostConfigurator();

        // Assert
        configurator.UsernameValue.Should().BeNull();
        configurator.PasswordValue.Should().BeNull();
        configurator.TlsConfigure.Should().BeNull();
    }
}
