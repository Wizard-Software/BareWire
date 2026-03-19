using System.Net.Security;
using AwesomeAssertions;
using BareWire.Transport.RabbitMQ.Configuration;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqTlsConfiguratorTests
{
    // ── Build_DefaultSettings ─────────────────────────────────────────────────

    [Fact]
    public void Build_DefaultSettings_EnablesSslWithServerName()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        SslOption result = configurator.Build("broker.example.com");

        // Assert
        result.Enabled.Should().BeTrue();
        result.ServerName.Should().Be("broker.example.com");
    }

    [Fact]
    public void Build_DefaultSettings_HasNoAcceptablePolicyErrors()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        SslOption result = configurator.Build("broker.example.com");

        // Assert — strict validation by default
        result.AcceptablePolicyErrors.Should().Be(SslPolicyErrors.None);
    }

    // ── Build_WithCertificate ─────────────────────────────────────────────────

    [Fact]
    public void Build_WithCertificate_SetsCertPathAndPassphrase()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();
        configurator.WithCertificate("/certs/client.pfx", "s3cr3t");

        // Act
        SslOption result = configurator.Build("broker.example.com");

        // Assert
        result.CertPath.Should().Be("/certs/client.pfx");
        result.CertPassphrase.Should().Be("s3cr3t");
    }

    [Fact]
    public void Build_WithCertificate_NullPassword_SetsEmptyPassphrase()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();
        configurator.WithCertificate("/certs/client.pfx", password: null);

        // Act
        SslOption result = configurator.Build("broker.example.com");

        // Assert
        result.CertPath.Should().Be("/certs/client.pfx");
        result.CertPassphrase.Should().BeEmpty();
    }

    [Fact]
    public void Build_WithCertificate_ReturnsThisForChaining()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        var returned = configurator.WithCertificate("/certs/client.pfx");

        // Assert — fluent chaining returns same instance
        returned.Should().BeSameAs(configurator);
    }

    // ── Build_WithServerValidation ────────────────────────────────────────────

    [Fact]
    public void Build_WithServerValidation_SetsAcceptablePolicyErrors()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();
        configurator.WithServerValidation(SslPolicyErrors.RemoteCertificateNameMismatch);

        // Act
        SslOption result = configurator.Build("broker.example.com");

        // Assert
        result.AcceptablePolicyErrors.Should().Be(SslPolicyErrors.RemoteCertificateNameMismatch);
    }

    [Fact]
    public void Build_WithServerValidation_AllErrors_SetsAllPolicyErrors()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();
        configurator.WithServerValidation(
            SslPolicyErrors.RemoteCertificateNotAvailable |
            SslPolicyErrors.RemoteCertificateNameMismatch |
            SslPolicyErrors.RemoteCertificateChainErrors);

        // Act
        SslOption result = configurator.Build("broker.example.com");

        // Assert
        result.AcceptablePolicyErrors.Should().HaveFlag(SslPolicyErrors.RemoteCertificateNotAvailable);
        result.AcceptablePolicyErrors.Should().HaveFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
        result.AcceptablePolicyErrors.Should().HaveFlag(SslPolicyErrors.RemoteCertificateChainErrors);
    }

    [Fact]
    public void Build_WithServerValidation_ReturnsThisForChaining()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        var returned = configurator.WithServerValidation(SslPolicyErrors.None);

        // Assert
        returned.Should().BeSameAs(configurator);
    }

    // ── Build_WithMutualAuthentication ────────────────────────────────────────

    [Fact]
    public void Build_WithMutualAuthentication_NoCertPath_DoesNotAddCerts()
    {
        // Arrange — WithMutualAuthentication called but no certificate path set.
        // The Certs collection must NOT be populated because no cert path was provided.
        var configurator = new RabbitMqTlsConfigurator();
        configurator.WithMutualAuthentication();

        // Act
        SslOption result = configurator.Build("broker.example.com");

        // Assert — no cert file to load, so Certs stays null or empty
        bool certsEmpty = result.Certs is null || result.Certs.Count == 0;
        certsEmpty.Should().BeTrue("no certificate path was provided so Certs must not be populated");
    }

    [Fact]
    public void Build_WithMutualAuthentication_ReturnsThisForChaining()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        var returned = configurator.WithMutualAuthentication();

        // Assert
        returned.Should().BeSameAs(configurator);
    }

    // ── WithCertificate validation ────────────────────────────────────────────

    [Fact]
    public void WithCertificate_NullPath_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        Action act = () => configurator.WithCertificate(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithCertificate_EmptyPath_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        Action act = () => configurator.WithCertificate(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── Build validation ──────────────────────────────────────────────────────

    [Fact]
    public void Build_NullServerName_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        Action act = () => configurator.Build(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_EmptyServerName_ThrowsArgumentException()
    {
        // Arrange
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        Action act = () => configurator.Build(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── Fluent chaining ───────────────────────────────────────────────────────

    [Fact]
    public void Build_FluentChain_CertificateAndValidation_AllOptionsApplied()
    {
        // Arrange — chain certificate path + server validation without mTLS
        // (mTLS cert loading requires a real file on disk; tested separately in integration tests)
        var configurator = new RabbitMqTlsConfigurator();

        // Act
        configurator
            .WithCertificate("/certs/client.pfx", "pass123")
            .WithServerValidation(SslPolicyErrors.RemoteCertificateNameMismatch);

        SslOption result = configurator.Build("broker.example.com");

        // Assert
        result.Enabled.Should().BeTrue();
        result.ServerName.Should().Be("broker.example.com");
        result.CertPath.Should().Be("/certs/client.pfx");
        result.CertPassphrase.Should().Be("pass123");
        result.AcceptablePolicyErrors.Should().Be(SslPolicyErrors.RemoteCertificateNameMismatch);
    }
}
