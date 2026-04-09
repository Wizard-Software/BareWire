using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization;
using NSubstitute;

namespace BareWire.UnitTests.Core.Serialization;

public sealed class DefaultSerializerResolverTests
{
    [Fact]
    public void Resolve_WhenCalled_ReturnsInjectedSerializer()
    {
        // Arrange
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        DefaultSerializerResolver sut = new(serializer);

        // Act
        IMessageSerializer result = sut.Resolve<object>();

        // Assert
        result.Should().BeSameAs(serializer);
    }

    [Fact]
    public void Resolve_WhenCalledForDifferentTypes_AlwaysReturnsSameInstance()
    {
        // Arrange
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        DefaultSerializerResolver sut = new(serializer);

        // Act
        IMessageSerializer result1 = sut.Resolve<string>();
        IMessageSerializer result2 = sut.Resolve<object>();
        IMessageSerializer result3 = sut.Resolve<DefaultSerializerResolverTests>();

        // Assert
        result1.Should().BeSameAs(serializer);
        result2.Should().BeSameAs(serializer);
        result3.Should().BeSameAs(serializer);
    }
}
