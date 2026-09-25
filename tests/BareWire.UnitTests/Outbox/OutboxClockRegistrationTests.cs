using AwesomeAssertions;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Outbox;

public sealed class OutboxClockRegistrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AddBareWireOutbox_WhenNoTimeProviderRegistered_RegistersSystemClockAndSharedJitter()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddBareWireOutbox(configureDbContext: o => o.UseSqlite("DataSource=:memory:"));

        // Assert
        services.Single(d => d.ServiceType == typeof(TimeProvider))
            .ImplementationInstance.Should().BeSameAs(TimeProvider.System);
        services.Single(d => d.ServiceType == typeof(IOutboxJitterSource))
            .ImplementationInstance.Should().BeSameAs(SharedRandomOutboxJitterSource.Instance);
    }

    [Fact]
    public async Task AddBareWireOutbox_WhenTimeProviderPreRegistered_StoreUsesUserClock()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        IOutboxJitterSource jitter = Substitute.For<IOutboxJitterSource>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(jitter);

        // Act
        services.AddBareWireOutbox(configureDbContext: o => o.UseSqlite("DataSource=:memory:"));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var store = (EfCoreOutboxStore)scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        // Assert
        store.TimeProvider.Should().BeSameAs(clock);
        store.JitterSource.Should().BeSameAs(jitter);
        services.Count(d => d.ServiceType == typeof(TimeProvider)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IOutboxJitterSource)).Should().Be(1);
    }

    [Fact]
    public void JitterSourceTypes_AreNotPartOfPublicApi()
    {
        typeof(IOutboxJitterSource).IsPublic.Should().BeFalse();
        typeof(SharedRandomOutboxJitterSource).IsPublic.Should().BeFalse();
    }
}
