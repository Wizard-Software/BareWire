using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.InMemory;
using BareWire.Serialization.Json;
using BareWire.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BareWire.UnitTests.Bundles;

/// <summary>
/// Unit tests for <see cref="ServiceCollectionExtensions.AddBareWireWithInMemory"/> — the
/// single-call registration bundle for the in-memory transport (task 20.26).
/// </summary>
public sealed class AddBareWireWithInMemoryTests
{
    private sealed record SampleMessage(int Id);

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireJsonSerializer();
        return services;
    }

    [Fact]
    public void AddBareWireWithInMemory_NullServices_ThrowsArgumentNullException()
    {
        IServiceCollection services = null!;

        Action act = () => services.AddBareWireWithInMemory(t => t.DefaultExchange(""));

        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }

    [Fact]
    public void AddBareWireWithInMemory_NullTransport_ThrowsArgumentNullException()
    {
        ServiceCollection services = NewServices();

        Action act = () => services.AddBareWireWithInMemory(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("transport");
    }

    [Fact]
    public void AddBareWireWithInMemory_SingleCall_RegistersInMemoryAdapterAndCoreBus()
    {
        ServiceCollection services = NewServices();

        services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange("");
            t.AutoDeclareEndpointQueues();
        });

        using ServiceProvider sp = services.BuildServiceProvider();

        sp.GetRequiredService<ITransportAdapter>().Should().BeOfType<InMemoryTransportAdapter>();
        sp.GetRequiredService<ITransportAdapter>().TransportName.Should().Be("InMemory");

        IBusControl control = sp.GetRequiredService<IBusControl>();
        control.Should().NotBeNull();
        sp.GetRequiredService<IBus>().Should().BeSameAs(control);
    }

    [Fact]
    public async Task AddBareWireWithInMemory_DefaultExchangeWithAutoDeclare_BusStartsAndStops()
    {
        ServiceCollection services = NewServices();

        services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange("");
            t.AutoDeclareEndpointQueues();
            t.DrainTimeout(TimeSpan.FromSeconds(1));
        });

        using ServiceProvider sp = services.BuildServiceProvider();
        IBusControl control = sp.GetRequiredService<IBusControl>();

        Func<Task> act = async () =>
        {
            await control.StartAsync(TestContext.Current.CancellationToken);
            await control.StopAsync(TestContext.Current.CancellationToken);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AddBareWireWithInMemory_SingleCall_RegistersExactlyOneCoreBus()
    {
        ServiceCollection services = NewServices();

        services.AddBareWireWithInMemory(t => t.DefaultExchange(""));

        services.Count(d => d.ServiceType == typeof(BareWireBusControl)).Should().Be(1);

        // Counted by implementation type, not service type: a sibling transport registration may
        // add its own unrelated IHostedService into the same collection.
        services.Count(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(BareWireBusHostedService))
            .Should().Be(1);
    }

    [Fact]
    public void AddBareWireWithInMemory_CalledTwice_ThrowsConfigurationException()
    {
        ServiceCollection services = NewServices();
        services.AddBareWireWithInMemory(t => t.DefaultExchange(""));

        Action act = () => services.AddBareWireWithInMemory(t => t.DefaultExchange(""));

        act.Should().Throw<BareWireConfigurationException>();
    }

    [Fact]
    public void AddBareWireWithInMemory_AfterAddBareWire_ThrowsConfigurationExceptionWithoutRegisteringTransport()
    {
        ServiceCollection services = NewServices();
        services.AddBareWire(_ => { });

        Action act = () => services.AddBareWireWithInMemory(t => t.DefaultExchange(""));

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*AddBareWire*");
        services.Should().NotContain(d => d.ServiceType == typeof(ITransportAdapter));
    }

    [Fact]
    public void AddBareWireWithInMemory_AfterAddBareWireInMemory_ThrowsConfigurationException()
    {
        ServiceCollection services = NewServices();
        services.AddBareWireInMemory(t => t.DefaultExchange(""));

        Action act = () => services.AddBareWireWithInMemory(t => t.DefaultExchange(""));

        act.Should().Throw<BareWireConfigurationException>();
        services.Should().NotContain(d => d.ServiceType == typeof(BusShutdownOptions));
    }

    [Fact]
    public void AddBareWireWithInMemory_BusShutdownOptionsAlreadyRegistered_ThrowsConfigurationException()
    {
        ServiceCollection services = NewServices();
        services.AddSingleton(new BusShutdownOptions());

        Action act = () => services.AddBareWireWithInMemory(t => t.DefaultExchange(""));

        act.Should().Throw<BareWireConfigurationException>();
    }

    [Fact]
    public void AddBareWireWithInMemory_DrainTimeoutConfigured_RegistersBusShutdownOptionsWithSameValue()
    {
        ServiceCollection services = NewServices();

        services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange("");
            t.DrainTimeout(TimeSpan.FromSeconds(3));
        });

        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<BusShutdownOptions>().DrainTimeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void AddBareWireWithInMemory_DrainTimeoutNotConfigured_RegistersDefaultBusShutdownOptions()
    {
        ServiceCollection services = NewServices();

        services.AddBareWireWithInMemory(t => t.DefaultExchange(""));

        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<BusShutdownOptions>().DrainTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AddBareWireWithInMemory_DrainTimeoutSetTwice_LastValueWins()
    {
        ServiceCollection services = NewServices();

        services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange("");
            t.DrainTimeout(TimeSpan.FromSeconds(3));
            t.DrainTimeout(TimeSpan.FromSeconds(7));
        });

        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<BusShutdownOptions>().DrainTimeout.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void AddBareWireWithInMemory_DrainTimeoutAboveTimerLimit_ThrowsConfigurationException()
    {
        ServiceCollection services = NewServices();

        Action act = () => services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange("");
            t.DrainTimeout(TimeSpan.MaxValue);
        });

        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>()
            .Which;
        ex.Message.Should().Contain("DrainTimeout");
        ex.InnerException.Should().BeOfType<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddBareWireWithInMemory_DrainTimeoutZero_ThrowsConfigurationException()
    {
        ServiceCollection services = NewServices();

        Action act = () => services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange("");
            t.DrainTimeout(TimeSpan.Zero);
        });

        act.Should().Throw<BareWireConfigurationException>();
    }

    [Fact]
    public async Task StartAsync_BundleContainerWithoutAdapterRegistration_ThrowsFriendlyConfigurationException()
    {
        ServiceCollection services = NewServices();
        services.AddBareWireWithInMemory(t => t.DefaultExchange(""));
        services.RemoveAll<ITransportAdapter>();

        using ServiceProvider sp = services.BuildServiceProvider();
        IBusControl control = sp.GetRequiredService<IBusControl>();

        Func<Task> act = async () => await control.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BareWireConfigurationException>();
    }

    [Fact]
    public void AddBareWireWithInMemory_TransportDelegate_ForwardsCallsToTransportConfigurator()
    {
        ServiceCollection services = NewServices();

        services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange("");
            t.MapRoutingKey<SampleMessage>("sample.key");
        });

        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<IRoutingKeyResolver>().Resolve<SampleMessage>().Should().Be("sample.key");
    }

    [Fact]
    public void AddBareWireWithInMemory_InvalidTransportOption_ThrowsConfigurationException()
    {
        ServiceCollection services = NewServices();

        Action act = () => services.AddBareWireWithInMemory(t =>
        {
            t.DefaultExchange("");
            t.QueueCapacity(0);
        });

        act.Should().Throw<BareWireConfigurationException>();
    }

    [Fact]
    public void AddBareWireWithInMemory_BusDelegate_IsInvoked()
    {
        ServiceCollection services = NewServices();
        var busDelegateInvoked = false;

        services.AddBareWireWithInMemory(
            t => t.DefaultExchange(""),
            bus => busDelegateInvoked = true);

        busDelegateInvoked.Should().BeTrue();
    }
}
