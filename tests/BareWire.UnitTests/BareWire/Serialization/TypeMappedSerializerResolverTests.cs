using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization;
using NSubstitute;

namespace BareWire.UnitTests.Core.Serialization;

public sealed class TypeMappedSerializerResolverTests
{
    private static IMessageSerializer MakeSerializer(string contentType)
    {
        IMessageSerializer s = Substitute.For<IMessageSerializer>();
        s.ContentType.Returns(contentType);
        return s;
    }

    [Fact]
    public void Resolve_WhenTypeIsMapped_ReturnsMappedSerializer()
    {
        // Arrange
        IMessageSerializer defaultSerializer = MakeSerializer("application/json");
        IMessageSerializer mappedSerializer = MakeSerializer("application/vnd.masstransit+json");

        var mappings = new Dictionary<Type, IMessageSerializer>
        {
            [typeof(MappedMessage)] = mappedSerializer,
        };

        TypeMappedSerializerResolver sut = new(defaultSerializer, mappings);

        // Act
        IMessageSerializer result = sut.Resolve<MappedMessage>();

        // Assert
        result.Should().BeSameAs(mappedSerializer);
    }

    [Fact]
    public void Resolve_WhenTypeIsNotMapped_ReturnsDefaultSerializer()
    {
        // Arrange
        IMessageSerializer defaultSerializer = MakeSerializer("application/json");
        IMessageSerializer mappedSerializer = MakeSerializer("application/vnd.masstransit+json");

        var mappings = new Dictionary<Type, IMessageSerializer>
        {
            [typeof(MappedMessage)] = mappedSerializer,
        };

        TypeMappedSerializerResolver sut = new(defaultSerializer, mappings);

        // Act
        IMessageSerializer result = sut.Resolve<UnmappedMessage>();

        // Assert
        result.Should().BeSameAs(defaultSerializer);
    }

    [Fact]
    public void Resolve_WhenMultipleTypesMapped_ReturnsCorrectPerType()
    {
        // Arrange
        IMessageSerializer defaultSerializer = MakeSerializer("application/json");
        IMessageSerializer serializerA = MakeSerializer("application/vnd.masstransit+json");
        IMessageSerializer serializerB = MakeSerializer("application/msgpack");

        var mappings = new Dictionary<Type, IMessageSerializer>
        {
            [typeof(MappedMessage)] = serializerA,
            [typeof(AnotherMappedMessage)] = serializerB,
        };

        TypeMappedSerializerResolver sut = new(defaultSerializer, mappings);

        // Act + Assert
        sut.Resolve<MappedMessage>().Should().BeSameAs(serializerA);
        sut.Resolve<AnotherMappedMessage>().Should().BeSameAs(serializerB);
        sut.Resolve<UnmappedMessage>().Should().BeSameAs(defaultSerializer);
    }

    [Fact]
    public void Ctor_WhenDefaultIsNull_ThrowsArgumentNull()
    {
        // Arrange
        var mappings = new Dictionary<Type, IMessageSerializer>();

        // Act
        Action act = () => _ = new TypeMappedSerializerResolver(null!, mappings);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("defaultSerializer");
    }

    // ── Stub message types ──────────────────────────────────────────────────────

    private sealed record MappedMessage(string Value);
    private sealed record AnotherMappedMessage(string Value);
    private sealed record UnmappedMessage(string Value);
}
