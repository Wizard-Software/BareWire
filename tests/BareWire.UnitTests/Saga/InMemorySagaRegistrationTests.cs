using AwesomeAssertions;
using BareWire.Abstractions.Saga;
using BareWire.Saga;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Saga;

public sealed class InMemorySagaRegistrationTests
{
    [Fact]
    public void AddBareWireSagaInMemory_ResolvesRepositoryWithValidateOnBuild()
    {
        var services = new ServiceCollection();
        services.AddBareWireSagaInMemory<RepositorySagaState>();

        // WebApplicationBuilder enables both validations in Development (GH #85).
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<ISagaRepository<RepositorySagaState>>();

        repository.Should().BeOfType<InMemorySagaRepository<RepositorySagaState>>();
    }

    [Fact]
    public async Task AddBareWireSagaInMemory_SharesStateAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddBareWireSagaInMemory<RepositorySagaState>();
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var saga = new RepositorySagaState { CorrelationId = Guid.NewGuid(), CurrentState = "Started" };

        // The saga dispatcher resolves the repository from a new scope for every message.
        using (IServiceScope first = provider.CreateScope())
        {
            await first.ServiceProvider.GetRequiredService<ISagaRepository<RepositorySagaState>>()
                .SaveAsync(saga, TestContext.Current.CancellationToken);
        }

        using IServiceScope second = provider.CreateScope();
        var found = await second.ServiceProvider.GetRequiredService<ISagaRepository<RepositorySagaState>>()
            .FindAsync(saga.CorrelationId, TestContext.Current.CancellationToken);

        found.Should().NotBeNull();
        found!.CurrentState.Should().Be("Started");
    }

    [Fact]
    public void AddBareWireSagaInMemory_WhenRepositoryAlreadyRegistered_KeepsExistingRegistration()
    {
        var existing = Substitute.For<ISagaRepository<RepositorySagaState>>();
        var services = new ServiceCollection();
        services.AddSingleton(existing);

        services.AddBareWireSagaInMemory<RepositorySagaState>();

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISagaRepository<RepositorySagaState>>().Should().BeSameAs(existing);
    }

    [Fact]
    public void AddBareWireSagaInMemory_NullServices_ThrowsArgumentNullException()
    {
        var act = () => BareWire.Saga.ServiceCollectionExtensions.AddBareWireSagaInMemory<RepositorySagaState>(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }
}
