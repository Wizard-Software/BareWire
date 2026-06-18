using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Configuration;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

public sealed class SqsTransportOptionsTests
{
    // ── Validate — Explicit mode ─────────────────────────────────────────────

    [Fact]
    public void Validate_ExplicitMode_WhenAccessKeyIdEmpty_ThrowsConfigurationException()
    {
        var options = new SqsTransportOptions
        {
            AuthMode = SqsAuthMode.Explicit,
            AccessKeyId = string.Empty,
            SecretAccessKey = "secret",
        };

        Action act = () => options.Validate();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(SqsTransportOptions.AccessKeyId));
    }

    [Fact]
    public void Validate_ExplicitMode_WhenSecretAccessKeyEmpty_ThrowsConfigurationException()
    {
        var options = new SqsTransportOptions
        {
            AuthMode = SqsAuthMode.Explicit,
            AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
            SecretAccessKey = string.Empty,
        };

        Action act = () => options.Validate();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(SqsTransportOptions.SecretAccessKey));
    }

    [Fact]
    public void Validate_ExplicitMode_WhenBothCredentialsSet_DoesNotThrow()
    {
        var options = new SqsTransportOptions
        {
            AuthMode = SqsAuthMode.Explicit,
            AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
            SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    // ── Validate — DefaultChain mode ─────────────────────────────────────────

    [Fact]
    public void Validate_DefaultChainMode_DoesNotRequireCredentials()
    {
        var options = new SqsTransportOptions
        {
            AuthMode = SqsAuthMode.DefaultChain,
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    // ── Validate — WaitTimeSeconds range ────────────────────────────────────

    [Fact]
    public void Validate_WaitTimeSeconds_MinusOne_ThrowsConfigurationException()
    {
        var options = new SqsTransportOptions { WaitTimeSeconds = -1 };

        Action act = () => options.Validate();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(SqsTransportOptions.WaitTimeSeconds));
    }

    [Fact]
    public void Validate_WaitTimeSeconds_TwentyOne_ThrowsConfigurationException()
    {
        var options = new SqsTransportOptions { WaitTimeSeconds = 21 };

        Action act = () => options.Validate();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(SqsTransportOptions.WaitTimeSeconds));
    }

    [Fact]
    public void Validate_WaitTimeSeconds_Zero_DoesNotThrow()
    {
        var options = new SqsTransportOptions { WaitTimeSeconds = 0 };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WaitTimeSeconds_Twenty_DoesNotThrow()
    {
        var options = new SqsTransportOptions { WaitTimeSeconds = 20 };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    // ── Validate — MaxNumberOfMessages range ─────────────────────────────────

    [Fact]
    public void Validate_MaxNumberOfMessages_Zero_ThrowsConfigurationException()
    {
        var options = new SqsTransportOptions { MaxNumberOfMessages = 0 };

        Action act = () => options.Validate();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(SqsTransportOptions.MaxNumberOfMessages));
    }

    [Fact]
    public void Validate_MaxNumberOfMessages_Eleven_ThrowsConfigurationException()
    {
        var options = new SqsTransportOptions { MaxNumberOfMessages = 11 };

        Action act = () => options.Validate();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(SqsTransportOptions.MaxNumberOfMessages));
    }

    [Fact]
    public void Validate_MaxNumberOfMessages_One_DoesNotThrow()
    {
        var options = new SqsTransportOptions { MaxNumberOfMessages = 1 };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MaxNumberOfMessages_Ten_DoesNotThrow()
    {
        var options = new SqsTransportOptions { MaxNumberOfMessages = 10 };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    // ── Validate — ServiceUrl TLS enforcement (SEC-01) ──────────────────────

    [Fact]
    public void Validate_HttpServiceUrl_WithoutAllowInsecure_ThrowsConfigurationException()
    {
        var options = new SqsTransportOptions
        {
            ServiceUrl = "http://localhost:4566",
            AllowInsecureEndpoint = false,
        };

        Action act = () => options.Validate();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(SqsTransportOptions.ServiceUrl));
    }

    [Fact]
    public void Validate_HttpServiceUrl_WithAllowInsecure_DoesNotThrow()
    {
        var options = new SqsTransportOptions
        {
            ServiceUrl = "http://localhost:4566",
            AllowInsecureEndpoint = true,
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_HttpsServiceUrl_DoesNotThrow()
    {
        var options = new SqsTransportOptions
        {
            ServiceUrl = "https://localhost:4566",
            AllowInsecureEndpoint = false,
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    // ── ToString redaction (SEC-02) ──────────────────────────────────────────

    [Fact]
    public void ToString_DoesNotContainSecretAccessKey()
    {
        const string sentinel = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
        var options = new SqsTransportOptions
        {
            AuthMode = SqsAuthMode.Explicit,
            AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
            SecretAccessKey = sentinel,
        };

        string rendered = options.ToString();

        rendered.Should().NotContain(sentinel,
            "SecretAccessKey must be redacted in ToString() (SEC-02)");
        rendered.Should().Contain("[Redacted]");
    }

    [Fact]
    public void ToString_ContainsNonSecretProperties()
    {
        var options = new SqsTransportOptions
        {
            AuthMode = SqsAuthMode.Explicit,
            AccessKeyId = "AKIAIOSFODNN7EXAMPLE",
            SecretAccessKey = "secret",
            RegionEndpoint = "eu-central-1",
            WaitTimeSeconds = 15,
            MaxNumberOfMessages = 5,
        };

        string rendered = options.ToString();

        rendered.Should().Contain("WaitTimeSeconds = 15");
        rendered.Should().Contain("MaxNumberOfMessages = 5");
        rendered.Should().Contain("eu-central-1");
        // AccessKeyId is an identifier (not a secret) — it may appear in output.
        rendered.Should().Contain("AKIAIOSFODNN7EXAMPLE");
    }

    // ── Default values ────────────────────────────────────────────────────────

    [Fact]
    public void DefaultValues_AreSetCorrectly()
    {
        var options = new SqsTransportOptions();

        options.AuthMode.Should().Be(SqsAuthMode.DefaultChain);
        options.WaitTimeSeconds.Should().Be(20);
        options.MaxNumberOfMessages.Should().Be(10);
        options.MaxInFlightMessages.Should().Be(100);
        options.DefaultVisibilityTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.AllowInsecureEndpoint.Should().BeFalse();
        options.EnableContentBasedDeduplication.Should().BeFalse();
    }

    // ── EnableContentBasedDeduplication (R4.2) ────────────────────────────────

    [Fact]
    public void ToString_ContainsEnableContentBasedDeduplication()
    {
        var options = new SqsTransportOptions
        {
            EnableContentBasedDeduplication = true,
        };

        string rendered = options.ToString();

        rendered.Should().Contain("EnableContentBasedDeduplication = True");
    }

    // ── SqsConfigurator.ContentBasedDeduplication() threads into options ──────

    [Fact]
    public void SqsConfigurator_ContentBasedDeduplication_ThreadsIntoOptions()
    {
        // Arrange — build options via the configurator with ContentBasedDeduplication() called.
        var configurator = new SqsConfigurator();
        configurator.ContentBasedDeduplication();

        // Act
        SqsTransportOptions options = configurator.Build();

        // Assert
        options.EnableContentBasedDeduplication.Should().BeTrue(
            "ContentBasedDeduplication() must set EnableContentBasedDeduplication = true in options");
    }

    [Fact]
    public void SqsConfigurator_WithoutContentBasedDeduplication_DefaultsFalse()
    {
        var configurator = new SqsConfigurator();

        SqsTransportOptions options = configurator.Build();

        options.EnableContentBasedDeduplication.Should().BeFalse();
    }
}
