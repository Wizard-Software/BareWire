using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization;
using NSubstitute;

namespace BareWire.UnitTests.Core.Serialization;

public sealed class SingleDeserializerResolverTests
{
    [Fact]
    public void Resolve_WithAnyContentType_AlwaysReturnsSameDeserializer()
    {
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        SingleDeserializerResolver sut = new(deserializer);

        sut.Resolve("application/json").Should().BeSameAs(deserializer);
        sut.Resolve("application/xml").Should().BeSameAs(deserializer);
        sut.Resolve(null).Should().BeSameAs(deserializer);
    }
}
