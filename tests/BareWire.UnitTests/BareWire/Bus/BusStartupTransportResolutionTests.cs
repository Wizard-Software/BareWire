using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Serialization.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Acceptance tests for task 15.3 (C1): when no <see cref="ITransportAdapter"/> is registered,
/// starting the bus must throw a friendly <see cref="BareWireConfigurationException"/> FIRST —
/// not the raw <see cref="InvalidOperationException"/> that <c>GetRequiredService</c> would throw
/// while the DI graph is being constructed.
/// </summary>
public sealed class BusStartupTransportResolutionTests
{
    private static ServiceProvider BuildProviderWithoutTransport()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireJsonSerializer();
        services.AddBareWire(_ => { });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task BusStartup_NoTransportRegistered_ThrowsFriendlyConfigurationException()
    {
        // Arrange — Core registered, but no ITransportAdapter in the container.
        await using ServiceProvider sp = BuildProviderWithoutTransport();

        // Act — resolving IBusControl must NOT throw (no eager GetRequiredService<ITransportAdapter>);
        // the friendly exception must surface from StartAsync via ConfigurationValidator.
        IBusControl control = sp.GetRequiredService<IBusControl>();
        Func<Task> act = async () => await control.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        BareWireConfigurationException ex =
            (await act.Should().ThrowAsync<BareWireConfigurationException>()).Which;
        ex.OptionName.Should().Be("Transport");
        ex.Message.Should().Contain("AddBareWireWithRabbitMq");
    }

    [Fact]
    public async Task BusStartup_NoTransportRegistered_DoesNotThrowRawInvalidOperationException()
    {
        // Arrange
        await using ServiceProvider sp = BuildProviderWithoutTransport();

        // Act — guards against the raw DI resolution exception ever winning the race against
        // the validator. The thrown type must be exactly BareWireConfigurationException.
        IBusControl control = sp.GetRequiredService<IBusControl>();
        Func<Task> act = async () => await control.StartAsync(TestContext.Current.CancellationToken);

        // Assert — must NOT be a raw InvalidOperationException from GetRequiredService.
        await act.Should().NotThrowAsync<InvalidOperationException>();
        await act.Should().ThrowAsync<BareWireConfigurationException>();
    }

    [Fact]
    public async Task BusStartup_WithInMemoryTransport_ResolvesAndValidates()
    {
        // Arrange — a registered ITransportAdapter (fake). The transport validation must pass;
        // StartAsync must not throw BareWireConfigurationException for Transport.
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("in-memory");
        adapter.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireJsonSerializer();
        services.AddSingleton(adapter);
        services.AddBareWire(_ => { });
        await using ServiceProvider sp = services.BuildServiceProvider();

        // Act
        IBusControl control = sp.GetRequiredService<IBusControl>();
        Func<Task> act = async () =>
        {
            await control.StartAsync(TestContext.Current.CancellationToken);
            await control.StopAsync(TestContext.Current.CancellationToken);
        };

        // Assert — transport validation passes (no Transport-related configuration exception).
        await act.Should().NotThrowAsync<BareWireConfigurationException>();
    }
}
