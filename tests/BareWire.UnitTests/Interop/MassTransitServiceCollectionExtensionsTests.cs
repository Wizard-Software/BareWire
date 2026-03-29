using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Interop.MassTransit;
using BareWire.Serialization.Json;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.UnitTests.Interop;

public sealed class MassTransitServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMassTransitEnvelopeDeserializer_ResolvesDeserializerResolver()
    {
        var services = new ServiceCollection();
        services.AddBareWireJsonSerializer();
        services.AddMassTransitEnvelopeDeserializer();

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetService<IDeserializerResolver>();

        resolver.Should().NotBeNull();
    }

    [Fact]
    public void AddMassTransitEnvelopeDeserializer_RouterResolvesMassTransitContentType()
    {
        var services = new ServiceCollection();
        services.AddBareWireJsonSerializer();
        services.AddMassTransitEnvelopeDeserializer();

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IDeserializerResolver>();

        var deserializer = resolver.Resolve("application/vnd.masstransit+json");

        deserializer.Should().NotBeNull();
        deserializer.ContentType.Should().Be("application/vnd.masstransit+json");
    }

    [Fact]
    public void AddMassTransitEnvelopeDeserializer_NullServices_ThrowsArgumentNull()
    {
        IServiceCollection? services = null;

        Action act = () => services!.AddMassTransitEnvelopeDeserializer();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddMassTransitEnvelopeDeserializer_WithoutJsonSerializer_ThrowsInvalidOperation()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddMassTransitEnvelopeDeserializer();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddBareWireJsonSerializer*");
    }
}
