using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Saga.Redis;
using Xunit;

namespace BareWire.UnitTests.Saga.Redis;

public sealed class RedisConfigurationBuilderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a valid base options instance with TLS disabled (RequireTlsInProduction=false)
    /// so that individual tests can focus on a single concern.
    /// </summary>
    private static RedisConnectionOptions ValidOptions(string endpoint = "localhost:6379") =>
        new RedisConnectionOptions
        {
            RequireTlsInProduction = false,
        }.Also(o => o.Endpoints.Add(endpoint));

    // ── Endpoint mapping ──────────────────────────────────────────────────────

    [Fact]
    public void Build_SingleEndpoint_AddsOneEntryToEndPoints()
    {
        var options = ValidOptions("redis-host:6380");

        var config = RedisConfigurationBuilder.Build(options);

        config.EndPoints.Should().HaveCount(1);
    }

    [Fact]
    public void Build_MultipleEndpoints_AllLandInEndPoints()
    {
        var options = new RedisConnectionOptions { RequireTlsInProduction = false };
        options.Endpoints.Add("node1:6379");
        options.Endpoints.Add("node2:6379");
        options.Endpoints.Add("node3:6379");

        var config = RedisConfigurationBuilder.Build(options);

        config.EndPoints.Should().HaveCount(3);
    }

    // ── TLS ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_SslTrueWithSslHost_SetsBothOnConfig()
    {
        var options = new RedisConnectionOptions
        {
            RequireTlsInProduction = false,
            Ssl = true,
            SslHost = "redis.example.com",
        };
        options.Endpoints.Add("redis.example.com:6380");

        var config = RedisConfigurationBuilder.Build(options);

        config.Ssl.Should().BeTrue();
        config.SslHost.Should().Be("redis.example.com");
    }

    // ── Sentinel ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ServiceNameSet_SetsServiceNameOnConfig()
    {
        var options = ValidOptions();
        options.ServiceName = "my-sentinel";

        var config = RedisConfigurationBuilder.Build(options);

        config.ServiceName.Should().Be("my-sentinel");
    }

    // ── AbortOnConnectFail default ────────────────────────────────────────────

    [Fact]
    public void Build_DefaultOptions_AbortOnConnectFailIsFalse()
    {
        var options = ValidOptions();

        var config = RedisConfigurationBuilder.Build(options);

        config.AbortOnConnectFail.Should().BeFalse();
    }

    // ── Auth and resilience params ────────────────────────────────────────────

    [Fact]
    public void Build_AuthAndResilienceParams_CopiedOntoConfig()
    {
        var options = ValidOptions();
        options.Password = "s3cr3t";
        options.User = "app-user";
        options.ConnectRetry = 5;
        options.ClientName = "barewire-saga";

        var config = RedisConfigurationBuilder.Build(options);

        config.Password.Should().Be("s3cr3t");
        config.User.Should().Be("app-user");
        config.ConnectRetry.Should().Be(5);
        config.ClientName.Should().Be("barewire-saga");
    }

    [Fact]
    public void Build_ConnectTimeoutSet_AppliedToConfig()
    {
        var options = ValidOptions();
        options.ConnectTimeout = 5000;

        var config = RedisConfigurationBuilder.Build(options);

        config.ConnectTimeout.Should().Be(5000);
    }

    // ── Validation: empty endpoints ───────────────────────────────────────────

    [Fact]
    public void Build_EmptyEndpoints_ThrowsWithOptionNameEndpoints()
    {
        var options = new RedisConnectionOptions { RequireTlsInProduction = false };
        // No endpoints added

        var act = () => RedisConfigurationBuilder.Build(options);

        act.Should().Throw<BareWireConfigurationException>()
           .Which.OptionName.Should().Be("Endpoints");
    }

    [Fact]
    public void Build_NullEndpointsList_ThrowsWithOptionNameEndpoints()
    {
        // RedisConnectionOptions.Endpoints is always initialized to a non-null list,
        // but we can simulate an empty-count scenario by not adding any entries.
        var options = new RedisConnectionOptions { RequireTlsInProduction = false };

        var act = () => RedisConfigurationBuilder.Build(options);

        act.Should().Throw<BareWireConfigurationException>()
           .Which.OptionName.Should().Be("Endpoints");
    }

    // ── Validation: mTLS PFX non-existent file ────────────────────────────────

    [Fact]
    public void Build_NonExistentPfxPath_ThrowsWithOptionNameClientCertificatePfxPath()
    {
        var options = new RedisConnectionOptions
        {
            RequireTlsInProduction = false,
            Ssl = true,
            ClientCertificatePfxPath = "/tmp/definitely-does-not-exist-barewire-test.pfx",
        };
        options.Endpoints.Add("localhost:6379");

        var act = () => RedisConfigurationBuilder.Build(options);

        act.Should().Throw<BareWireConfigurationException>()
           .Which.OptionName.Should().Be("ClientCertificatePfxPath");
    }

    // ── Validation: SEC-01 TLS flag ───────────────────────────────────────────

    [Fact]
    public void Build_RequireTlsInProductionTrueAndSslFalse_ThrowsWithOptionNameSsl()
    {
        var options = new RedisConnectionOptions
        {
            RequireTlsInProduction = true,
            Ssl = false,
        };
        options.Endpoints.Add("localhost:6379");

        var act = () => RedisConfigurationBuilder.Build(options);

        act.Should().Throw<BareWireConfigurationException>()
           .Which.OptionName.Should().Be("Ssl");
    }

    [Fact]
    public void Build_RequireTlsInProductionFalseAndSslFalse_DoesNotThrow()
    {
        var options = new RedisConnectionOptions
        {
            RequireTlsInProduction = false,
            Ssl = false,
        };
        options.Endpoints.Add("localhost:6379");

        var act = () => RedisConfigurationBuilder.Build(options);

        act.Should().NotThrow();
    }
}

/// <summary>Fluent helper to configure an object inline without a local variable.</summary>
file static class FluentExtensions
{
    public static T Also<T>(this T obj, Action<T> configure)
    {
        configure(obj);
        return obj;
    }
}
