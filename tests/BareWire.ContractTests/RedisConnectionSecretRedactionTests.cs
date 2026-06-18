using AwesomeAssertions;

using BareWire.Abstractions.Exceptions;
using BareWire.Saga.Redis;

using Xunit;

namespace BareWire.ContractTests;

/// <summary>
/// Verifies that secrets are never exposed through diagnostic surfaces of the Redis connection
/// configuration (SEC-06: secret redaction in ToString; §0 dec. 3 guard on BareWireConfigurationException).
/// </summary>
public sealed class RedisConnectionSecretRedactionTests
{
    [Fact]
    public void RedisConnectionOptions_ToString_DoesNotContainPasswordSecret()
    {
        var options = new RedisConnectionOptions
        {
            Password = "s3cret-pw",
        };
        options.Endpoints.Add("h:6379");

        var text = options.ToString();

        text.Should().NotContain("s3cret-pw");
    }

    [Fact]
    public void RedisConnectionOptions_ToString_DoesNotContainPfxPasswordSecret()
    {
        var options = new RedisConnectionOptions
        {
            ClientCertificatePfxPassword = "pfx-s3cret",
        };
        options.Endpoints.Add("h:6379");

        var text = options.ToString();

        text.Should().NotContain("pfx-s3cret");
    }

    [Fact]
    public void RedisConfigurationBuilder_Build_ExceptionMessageDoesNotContainSecrets()
    {
        // Arrange: options that trigger SEC-01 (RequireTlsInProduction=true + Ssl=false).
        // Secrets are present on the options to confirm they never leak into the exception message.
        var options = new RedisConnectionOptions
        {
            RequireTlsInProduction = true,
            Ssl = false,
            Password = "leak-me",
            ClientCertificatePfxPassword = "leak-me-2",
        };
        options.Endpoints.Add("localhost:6379");

        // Act
        BareWireConfigurationException? thrown = null;
        try
        {
            RedisConfigurationBuilder.Build(options);
        }
        catch (BareWireConfigurationException ex)
        {
            thrown = ex;
        }

        // Assert
        thrown.Should().NotBeNull("Build should throw BareWireConfigurationException for RequireTlsInProduction=true + Ssl=false");
        thrown!.Message.Should().NotContain("leak-me");
        thrown.Message.Should().NotContain("leak-me-2");
    }
}
