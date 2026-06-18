using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Saga.Redis;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace BareWire.UnitTests.Saga.Redis;

public sealed class RedisConnectionRegistrationTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Action<RedisConnectionOptions> ValidConfigure =>
        opts =>
        {
            opts.Endpoints.Add("localhost:6379");
            opts.Ssl = true;
        };

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public void AddBareWireRedisConnection_RegistersIConnectionMultiplexerAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddBareWireRedisConnection(ValidConfigure);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddBareWireRedisConnection_CalledTwice_DoesNotAddSecondDescriptor()
    {
        var services = new ServiceCollection();

        services.AddBareWireRedisConnection(ValidConfigure);
        services.AddBareWireRedisConnection(ValidConfigure);

        var count = services.Count(d => d.ServiceType == typeof(IConnectionMultiplexer));
        count.Should().Be(1);
    }

    // ── Null guards ───────────────────────────────────────────────────────────

    [Fact]
    public void AddBareWireRedisConnection_NullServices_ThrowsArgumentNullException()
    {
        IServiceCollection services = null!;

        var act = () => services.AddBareWireRedisConnection(ValidConfigure);

        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }

    [Fact]
    public void AddBareWireRedisConnection_NullConfigure_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        var act = () => services.AddBareWireRedisConnection(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }

    // ── Eager validation ──────────────────────────────────────────────────────

    [Fact]
    public void AddBareWireRedisConnection_EmptyEndpoints_ThrowsBareWireConfigurationExceptionEagerly()
    {
        var services = new ServiceCollection();

        // The exception must be thrown when calling AddBareWireRedisConnection, not when building/resolving.
        var act = () => services.AddBareWireRedisConnection(opts =>
        {
            opts.RequireTlsInProduction = false;
            // Intentionally no endpoints added
        });

        act.Should().Throw<BareWireConfigurationException>()
           .Which.OptionName.Should().Be("Endpoints");
    }
}
