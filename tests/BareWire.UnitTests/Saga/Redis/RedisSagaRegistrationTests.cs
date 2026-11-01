using AwesomeAssertions;
using BareWire.Abstractions.Saga;
using BareWire.Saga.Redis;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BareWire.UnitTests.Saga.Redis;

public sealed class RedisSagaRegistrationTests
{
    [Fact]
    public void AddBareWireSagaRedis_WithMultiplexerRegistered_ResolvesRepositoryWithValidateOnBuild()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Substitute.For<IDatabase>());

        var services = new ServiceCollection();
        services.AddSingleton(multiplexer);
        services.AddBareWireSagaRedis<RedisSagaState>(options => options.KeyPrefix = "saga");

        // WebApplicationBuilder enables both validations in Development (GH #85).
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<ISagaRepository<RedisSagaState>>();

        repository.Should().BeOfType<RedisSagaRepository<RedisSagaState>>();
    }
}
