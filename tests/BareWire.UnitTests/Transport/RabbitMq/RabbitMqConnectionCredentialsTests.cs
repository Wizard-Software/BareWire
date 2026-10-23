using AwesomeAssertions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Verifies that <see cref="RabbitMqConnectionCredentials"/> applies the configured credential overrides on top of
/// the credentials taken from the connection string. Only <c>UserName</c> and <c>Password</c> are asserted; the
/// factory <c>Uri</c> getter rebuilds the URI including the password and is never read.
/// </summary>
public sealed class RabbitMqConnectionCredentialsTests
{
    private static ConnectionFactory FactoryFromUri() =>
        new() { Uri = new Uri("amqp://uriuser:uripass@localhost:5672/") };

    [Fact]
    public void ApplyOverrides_WhenBothOverridesSet_ReplacesUriCredentials()
    {
        ConnectionFactory factory = FactoryFromUri();
        var options = new RabbitMqTransportOptions
        {
            UsernameOverride = "override-user",
            PasswordOverride = "override-pass",
        };

        RabbitMqConnectionCredentials.ApplyOverrides(factory, options);

        factory.UserName.Should().Be("override-user");
        factory.Password.Should().Be("override-pass");
    }

    [Fact]
    public void ApplyOverrides_WhenOnlyUsernameSet_KeepsUriPassword()
    {
        ConnectionFactory factory = FactoryFromUri();
        var options = new RabbitMqTransportOptions { UsernameOverride = "override-user" };

        RabbitMqConnectionCredentials.ApplyOverrides(factory, options);

        factory.UserName.Should().Be("override-user");
        factory.Password.Should().Be("uripass");
    }

    [Fact]
    public void ApplyOverrides_WhenOnlyPasswordSet_KeepsUriUsername()
    {
        ConnectionFactory factory = FactoryFromUri();
        var options = new RabbitMqTransportOptions { PasswordOverride = "override-pass" };

        RabbitMqConnectionCredentials.ApplyOverrides(factory, options);

        factory.UserName.Should().Be("uriuser");
        factory.Password.Should().Be("override-pass");
    }

    [Fact]
    public void ApplyOverrides_WhenNoOverrides_LeavesUriCredentials()
    {
        ConnectionFactory factory = FactoryFromUri();

        RabbitMqConnectionCredentials.ApplyOverrides(factory, new RabbitMqTransportOptions());

        factory.UserName.Should().Be("uriuser");
        factory.Password.Should().Be("uripass");
    }

    [Fact]
    public void ApplyOverrides_WhenUriHasNoUserInfo_AppliesOverridesOverDefaults()
    {
        var factory = new ConnectionFactory { Uri = new Uri("amqp://localhost:5672/") };
        var options = new RabbitMqTransportOptions
        {
            UsernameOverride = "override-user",
            PasswordOverride = "override-pass",
        };

        RabbitMqConnectionCredentials.ApplyOverrides(factory, options);

        factory.UserName.Should().Be("override-user");
        factory.Password.Should().Be("override-pass");
    }
}
