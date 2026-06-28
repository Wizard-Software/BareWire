using AwesomeAssertions;
using BareWire.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BareWire.UnitTests.Outbox;

public sealed class OutboxSchemaInitializerRegistrationTests
{
    [Fact]
    public void AddBareWireOutbox_WhenAutoCreateSchemaTrue_RegistersInitializer()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddBareWireOutbox(
            configureDbContext: o => o.UseSqlite("DataSource=:memory:"),
            configureOutbox: outbox => { outbox.AutoCreateSchema = true; });

        // Assert
        var hasInitializer = services.Any(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType is not null
            && d.ImplementationType.Name == "OutboxSchemaInitializer");

        hasInitializer.Should().BeTrue(
            "OutboxSchemaInitializer should be registered as IHostedService when AutoCreateSchema is true");
    }

    [Fact]
    public void AddBareWireOutbox_WhenAutoCreateSchemaFalse_DoesNotRegisterInitializer()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act — configureOutbox is omitted; AutoCreateSchema defaults to false
        services.AddBareWireOutbox(
            configureDbContext: o => o.UseSqlite("DataSource=:memory:"));

        // Assert
        var hasInitializer = services.Any(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType is not null
            && d.ImplementationType.Name == "OutboxSchemaInitializer");

        hasInitializer.Should().BeFalse(
            "OutboxSchemaInitializer must not be registered when AutoCreateSchema is false");
    }

    // Codex adversarial review: the dispatcher now polls immediately on StartAsync (no initial delay).
    // IHostedService.StartAsync runs sequentially in registration order, each awaited before the next,
    // so the schema initializer must be registered BEFORE the dispatcher — otherwise a fresh-database
    // first GetPendingAsync races table creation under AutoCreateSchema.
    [Fact]
    public void AddBareWireOutbox_WhenAutoCreateSchemaTrue_RegistersInitializerBeforeDispatcher()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddBareWireOutbox(
            configureDbContext: o => o.UseSqlite("DataSource=:memory:"),
            configureOutbox: outbox => { outbox.AutoCreateSchema = true; });

        // Assert
        var hosted = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .ToList();
        int initializerIndex = hosted.FindIndex(d => d.ImplementationType?.Name == "OutboxSchemaInitializer");
        int dispatcherIndex = hosted.FindIndex(d => d.ImplementationType?.Name == "OutboxDispatcher");

        initializerIndex.Should().BeGreaterThanOrEqualTo(0,
            "OutboxSchemaInitializer must be registered when AutoCreateSchema is true");
        dispatcherIndex.Should().BeGreaterThanOrEqualTo(0, "OutboxDispatcher must be registered");
        initializerIndex.Should().BeLessThan(dispatcherIndex,
            "OutboxSchemaInitializer must start before OutboxDispatcher so the tables exist before the dispatcher's first poll");
    }
}
