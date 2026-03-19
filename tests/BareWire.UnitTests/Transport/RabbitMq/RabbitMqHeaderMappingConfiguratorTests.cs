using AwesomeAssertions;
using BareWire.Transport.RabbitMQ;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqHeaderMappingConfiguratorTests
{
    private static RabbitMqHeaderMappingConfigurator CreateConfigurator() =>
        new();

    // ── MapCorrelationId ──────────────────────────────────────────────────────

    [Fact]
    public void MapCorrelationId_ValidName_SetsCorrelationIdMapping()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MapCorrelationId("X-Correlation-ID");

        // Assert
        sut.CorrelationIdMapping.Should().Be("X-Correlation-ID");
    }

    [Fact]
    public void MapCorrelationId_CalledTwice_LastValueWins()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MapCorrelationId("first-corr-id");
        sut.MapCorrelationId("second-corr-id");

        // Assert
        sut.CorrelationIdMapping.Should().Be("second-corr-id");
    }

    [Fact]
    public void MapCorrelationId_NullOrEmpty_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action actNull = () => sut.MapCorrelationId(null!);
        Action actEmpty = () => sut.MapCorrelationId(string.Empty);

        // Assert
        actNull.Should().Throw<ArgumentNullException>();
        actEmpty.Should().Throw<ArgumentException>();
    }

    // ── MapMessageType ────────────────────────────────────────────────────────

    [Fact]
    public void MapMessageType_ValidName_SetsMessageTypeMapping()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MapMessageType("X-Message-Type");

        // Assert
        sut.MessageTypeMapping.Should().Be("X-Message-Type");
    }

    [Fact]
    public void MapMessageType_NullOrEmpty_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action actNull = () => sut.MapMessageType(null!);
        Action actEmpty = () => sut.MapMessageType(string.Empty);

        // Assert
        actNull.Should().Throw<ArgumentNullException>();
        actEmpty.Should().Throw<ArgumentException>();
    }

    // ── MapHeader ─────────────────────────────────────────────────────────────

    [Fact]
    public void MapHeader_ValidMapping_AddsToCustomMappings()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MapHeader("BW-TenantId", "X-TenantId");

        // Assert
        sut.CustomMappings.Should().ContainKey("BW-TenantId")
            .WhoseValue.Should().Be("X-TenantId");
    }

    [Fact]
    public void MapHeader_MultipleMappings_AccumulatesAll()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MapHeader("BW-TenantId", "X-TenantId");
        sut.MapHeader("BW-RequestId", "X-RequestId");
        sut.MapHeader("BW-UserId", "X-UserId");

        // Assert
        sut.CustomMappings.Should().HaveCount(3);
        sut.CustomMappings["BW-TenantId"].Should().Be("X-TenantId");
        sut.CustomMappings["BW-RequestId"].Should().Be("X-RequestId");
        sut.CustomMappings["BW-UserId"].Should().Be("X-UserId");
    }

    [Fact]
    public void MapHeader_DuplicateKey_OverwritesPreviousValue()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.MapHeader("BW-TenantId", "X-TenantId-Old");
        sut.MapHeader("BW-TenantId", "X-TenantId-New");

        // Assert
        sut.CustomMappings["BW-TenantId"].Should().Be("X-TenantId-New");
        sut.CustomMappings.Should().HaveCount(1);
    }

    [Fact]
    public void MapHeader_NullBareWireHeader_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.MapHeader(null!, "X-Something");

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MapHeader_NullTransportHeader_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.MapHeader("BW-Something", null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ── IgnoreUnmappedHeaders ─────────────────────────────────────────────────

    [Fact]
    public void IgnoreUnmappedHeaders_DefaultFalse_PassthroughEnabled()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Assert
        sut.ShouldIgnoreUnmapped.Should().BeFalse();
    }

    [Fact]
    public void IgnoreUnmappedHeaders_CalledWithTrue_EnablesWhitelistMode()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.IgnoreUnmappedHeaders(true);

        // Assert
        sut.ShouldIgnoreUnmapped.Should().BeTrue();
    }

    [Fact]
    public void IgnoreUnmappedHeaders_CalledWithNoArgument_DefaultsToTrue()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.IgnoreUnmappedHeaders();

        // Assert
        sut.ShouldIgnoreUnmapped.Should().BeTrue();
    }

    [Fact]
    public void IgnoreUnmappedHeaders_CalledWithFalse_DisablesWhitelistMode()
    {
        // Arrange
        var sut = CreateConfigurator();
        sut.IgnoreUnmappedHeaders(true);

        // Act
        sut.IgnoreUnmappedHeaders(false);

        // Assert
        sut.ShouldIgnoreUnmapped.Should().BeFalse();
    }

    // ── DefaultState ──────────────────────────────────────────────────────────

    [Fact]
    public void DefaultState_AllPropertiesAreNull_OrEmpty()
    {
        // Arrange & Act
        var sut = CreateConfigurator();

        // Assert
        sut.CorrelationIdMapping.Should().BeNull();
        sut.MessageTypeMapping.Should().BeNull();
        sut.CustomMappings.Should().BeEmpty();
        sut.ShouldIgnoreUnmapped.Should().BeFalse();
    }
}
