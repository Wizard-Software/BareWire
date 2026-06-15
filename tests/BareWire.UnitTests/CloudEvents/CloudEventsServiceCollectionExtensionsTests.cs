using AwesomeAssertions;

using BareWire.Abstractions.Serialization;
using BareWire.CloudEvents;
using BareWire.Serialization.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Tests for <see cref="ServiceCollectionExtensions.AddCloudEvents"/>.
/// </summary>
public sealed class CloudEventsServiceCollectionExtensionsTests
{
    // -------------------------------------------------------------------------
    // Happy path — resolve after registration
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEvents_WhenJsonSerializerRegistered_RegistersBinaryActivation()
    {
        var services = new ServiceCollection();
        services.AddBareWireJsonSerializer();
        services.AddCloudEvents();

        using ServiceProvider provider = services.BuildServiceProvider();
        CloudEventsBinaryActivation? activation = provider.GetService<CloudEventsBinaryActivation>();

        activation.Should().NotBeNull();
    }

    // -------------------------------------------------------------------------
    // Guard: IMessageSerializer must be registered first
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEvents_WhenJsonSerializerMissing_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddCloudEvents();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddBareWireJsonSerializer*");
    }

    // -------------------------------------------------------------------------
    // ADR-001: default serializer must NOT be replaced
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEvents_DoesNotReplaceDefaultMessageSerializer()
    {
        var services = new ServiceCollection();
        // Register a specific stub instance so we can check identity.
        IMessageSerializer stubSerializer = Substitute.For<IMessageSerializer>();
        services.AddSingleton(stubSerializer);

        services.AddCloudEvents();

        using ServiceProvider provider = services.BuildServiceProvider();
        IMessageSerializer resolved = provider.GetRequiredService<IMessageSerializer>();

        resolved.Should().BeSameAs(stubSerializer);
        // Assert only ONE IMessageSerializer descriptor exists (no double-registration).
        services.Count(d => d.ServiceType == typeof(IMessageSerializer)).Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // Null-guard
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEvents_WhenServicesNull_ThrowsArgumentNullException()
    {
        IServiceCollection? services = null;

        Action act = () => services!.AddCloudEvents();

        act.Should().Throw<ArgumentNullException>();
    }

    // -------------------------------------------------------------------------
    // Idempotency: TryAddSingleton — second call must not duplicate the registration
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEvents_CalledTwice_RegistersActivationOnce()
    {
        var services = new ServiceCollection();
        services.AddBareWireJsonSerializer();
        services.AddCloudEvents();
        services.AddCloudEvents(); // second call — must be idempotent

        int count = services.Count(d => d.ServiceType == typeof(CloudEventsBinaryActivation));

        count.Should().Be(1);
    }
}
