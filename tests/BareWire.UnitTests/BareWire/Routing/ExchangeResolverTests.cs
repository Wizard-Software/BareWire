using AwesomeAssertions;
using BareWire.Routing;

namespace BareWire.UnitTests.Core.Routing;

public sealed record ExchangeDummyMessage;
public sealed record OtherExchangeMessage;

public sealed class ExchangeResolverTests
{
    // ── Resolve_WhenTypeMapped ────────────────────────────────────────────────

    [Fact]
    public void Resolve_WhenTypeMapped_ReturnsMappedExchange()
    {
        // Arrange
        var mappings = new Dictionary<Type, string>
        {
            [typeof(ExchangeDummyMessage)] = "payments.topic",
        };
        var resolver = new ExchangeResolver(mappings);

        // Act
        string? result = resolver.Resolve<ExchangeDummyMessage>();

        // Assert
        result.Should().Be("payments.topic");
    }

    // ── Resolve_WhenTypeNotMapped ─────────────────────────────────────────────

    [Fact]
    public void Resolve_WhenTypeNotMapped_ReturnsNull()
    {
        // Arrange — mappings do not contain OtherExchangeMessage
        var mappings = new Dictionary<Type, string>
        {
            [typeof(ExchangeDummyMessage)] = "payments.topic",
        };
        var resolver = new ExchangeResolver(mappings);

        // Act
        string? result = resolver.Resolve<OtherExchangeMessage>();

        // Assert
        result.Should().BeNull();
    }

    // ── Resolve_WithNullMappings ──────────────────────────────────────────────

    [Fact]
    public void Resolve_WithNullMappings_ReturnsNull()
    {
        // Arrange — null passed to constructor → treated as empty
        var resolver = new ExchangeResolver(null);

        // Act
        string? result = resolver.Resolve<ExchangeDummyMessage>();

        // Assert
        result.Should().BeNull();
    }

    // ── Resolve_WithEmptyMappings ─────────────────────────────────────────────

    [Fact]
    public void Resolve_WithEmptyMappings_ReturnsNull()
    {
        // Arrange — explicitly empty dictionary passed
        var resolver = new ExchangeResolver(new Dictionary<Type, string>());

        // Act
        string? result = resolver.Resolve<ExchangeDummyMessage>();

        // Assert
        result.Should().BeNull();
    }
}
