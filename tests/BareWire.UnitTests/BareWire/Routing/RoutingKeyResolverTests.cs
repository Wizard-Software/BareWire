using AwesomeAssertions;
using BareWire.Routing;

namespace BareWire.UnitTests.Core.Routing;

public sealed record DummyMessage;

public sealed class RoutingKeyResolverTests
{
    // ── Resolve_WithRegisteredMapping ─────────────────────────────────────────

    [Fact]
    public void Resolve_WithRegisteredMapping_ReturnsMappedKey()
    {
        // Arrange
        var mappings = new Dictionary<Type, string>
        {
            [typeof(DummyMessage)] = "order.created",
        };
        var resolver = new RoutingKeyResolver(mappings);

        // Act
        string result = resolver.Resolve<DummyMessage>();

        // Assert
        result.Should().Be("order.created");
    }

    // ── Resolve_WithoutMapping ────────────────────────────────────────────────

    [Fact]
    public void Resolve_WithoutMapping_ReturnsFullTypeName()
    {
        // Arrange — no mapping registered for DummyMessage
        var resolver = new RoutingKeyResolver();

        // Act
        string result = resolver.Resolve<DummyMessage>();

        // Assert
        result.Should().Be(typeof(DummyMessage).FullName);
    }

    // ── Resolve_WithEmptyMappings ─────────────────────────────────────────────

    [Fact]
    public void Resolve_WithEmptyMappings_ReturnsFullTypeName()
    {
        // Arrange — explicitly empty dictionary passed
        var resolver = new RoutingKeyResolver(new Dictionary<Type, string>());

        // Act
        string result = resolver.Resolve<DummyMessage>();

        // Assert
        result.Should().Be(typeof(DummyMessage).FullName);
    }
}
