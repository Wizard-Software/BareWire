using AwesomeAssertions;

using BareWire.Abstractions.Serialization;
using BareWire.CloudEvents;
using BareWire.Serialization.Json;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Tests for <see cref="ServiceCollectionExtensions.AddCloudEventsEnvelope"/>.
/// </summary>
public sealed class CloudEventsEnvelopeServiceCollectionExtensionsTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddBareWireJsonSerializer();
        services.AddCloudEvents();
        return services;
    }

    // -------------------------------------------------------------------------
    // Happy path — routing application/cloudevents+json to envelope deserializer
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEventsEnvelope_RoutesApplicationCloudEventsJson_ToEnvelopeDeserializer()
    {
        ServiceCollection services = CreateServices();
        services.AddCloudEventsEnvelope();

        using ServiceProvider provider = services.BuildServiceProvider();
        IDeserializerResolver resolver = provider.GetRequiredService<IDeserializerResolver>();
        IMessageDeserializer resolved = resolver.Resolve("application/cloudevents+json");

        resolved.Should().BeOfType<CloudEventsEnvelopeDeserializer>();
    }

    // -------------------------------------------------------------------------
    // ADR-001: raw-JSON path must NOT be affected
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEventsEnvelope_DoesNotAffectDefaultRawJsonRoute()
    {
        ServiceCollection services = CreateServices();
        services.AddCloudEventsEnvelope();

        using ServiceProvider provider = services.BuildServiceProvider();
        IDeserializerResolver resolver = provider.GetRequiredService<IDeserializerResolver>();

        // application/json and null (default) must still resolve to the raw JSON deserializer,
        // not the CloudEvents envelope deserializer.
        IMessageDeserializer resolvedJson = resolver.Resolve("application/json");
        IMessageDeserializer resolvedNull = resolver.Resolve(null);

        resolvedJson.Should().NotBeOfType<CloudEventsEnvelopeDeserializer>();
        resolvedNull.Should().NotBeOfType<CloudEventsEnvelopeDeserializer>();
        resolvedJson.Should().BeOfType<SystemTextJsonRawDeserializer>();
        resolvedNull.Should().BeOfType<SystemTextJsonRawDeserializer>();
    }

    // -------------------------------------------------------------------------
    // ADR-001: IMessageSerializer must NOT be replaced
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEventsEnvelope_DoesNotReplaceDefaultMessageSerializer()
    {
        ServiceCollection services = CreateServices();
        services.AddCloudEventsEnvelope();

        using ServiceProvider provider = services.BuildServiceProvider();

        // Exactly one IMessageSerializer descriptor must exist.
        services.Count(d => d.ServiceType == typeof(IMessageSerializer)).Should().Be(1);

        // The resolved type must still be the default raw-JSON serializer.
        IMessageSerializer resolved = provider.GetRequiredService<IMessageSerializer>();
        resolved.Should().BeOfType<SystemTextJsonSerializer>();
    }

    // -------------------------------------------------------------------------
    // Guard: AddBareWireJsonSerializer() must be called first
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEventsEnvelope_WhenJsonSerializerMissing_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddCloudEventsEnvelope();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddBareWireJsonSerializer*");
    }

    // -------------------------------------------------------------------------
    // Null-guard
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEventsEnvelope_WhenServicesNull_ThrowsArgumentNullException()
    {
        IServiceCollection? services = null;

        Action act = () => services!.AddCloudEventsEnvelope();

        act.Should().Throw<ArgumentNullException>();
    }

    // -------------------------------------------------------------------------
    // Idempotency: second call must not stack decorators or duplicate registrations
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEventsEnvelope_CalledTwice_DoesNotStackDecoratorsAndRegistersDeserializerOnce()
    {
        ServiceCollection services = CreateServices();
        services.AddCloudEventsEnvelope();
        services.AddCloudEventsEnvelope(); // second call — must be idempotent

        // Only one IDeserializerResolver descriptor must exist.
        services.Count(d => d.ServiceType == typeof(IDeserializerResolver)).Should().Be(1);

        // Only one CloudEventsEnvelopeDeserializer descriptor must exist.
        services.Count(d => d.ServiceType == typeof(CloudEventsEnvelopeDeserializer)).Should().Be(1);

        using ServiceProvider provider = services.BuildServiceProvider();
        IDeserializerResolver resolver = provider.GetRequiredService<IDeserializerResolver>();

        // Routing still works correctly after two calls.
        resolver.Resolve("application/cloudevents+json").Should().BeOfType<CloudEventsEnvelopeDeserializer>();
        resolver.Resolve("application/json").Should().NotBeOfType<CloudEventsEnvelopeDeserializer>();
    }

    // -------------------------------------------------------------------------
    // Marker singleton registered
    // -------------------------------------------------------------------------

    [Fact]
    public void AddCloudEventsEnvelope_RegistersEnvelopeActivationMarker()
    {
        ServiceCollection services = CreateServices();
        services.AddCloudEventsEnvelope();

        using ServiceProvider provider = services.BuildServiceProvider();
        CloudEventsEnvelopeActivation? activation = provider.GetService<CloudEventsEnvelopeActivation>();

        activation.Should().NotBeNull();
    }
}
