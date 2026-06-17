using Azure.Messaging.ServiceBus;
using AwesomeAssertions;
using BareWire.Transport.AzureServiceBus;
using Xunit;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

public sealed class AzureServiceBusHeaderMapperTests
{
    // ── MapOutbound ──────────────────────────────────────────────────────────

    [Fact]
    public void MapOutbound_EmptyDictionary_SetsNoApplicationProperties()
    {
        // Arrange
        var headers = new Dictionary<string, string>();
        var message = new ServiceBusMessage(BinaryData.Empty);

        // Act
        AzureServiceBusHeaderMapper.MapOutbound(headers, message);

        // Assert
        message.ApplicationProperties.Should().BeEmpty();
    }

    [Fact]
    public void MapOutbound_WithMultipleHeaders_CopiesAllAsApplicationProperties()
    {
        // Arrange
        var headers = new Dictionary<string, string>
        {
            ["message-id"] = "abc-123",
            ["correlation-id"] = "xyz-456",
            ["content-type"] = "application/json",
        };
        var message = new ServiceBusMessage(BinaryData.Empty);

        // Act
        AzureServiceBusHeaderMapper.MapOutbound(headers, message);

        // Assert
        message.ApplicationProperties.Should().HaveCount(3);
        message.ApplicationProperties["message-id"].Should().Be("abc-123");
        message.ApplicationProperties["correlation-id"].Should().Be("xyz-456");
        message.ApplicationProperties["content-type"].Should().Be("application/json");
    }

    [Fact]
    public void MapOutbound_SingleHeader_IsPresent()
    {
        // Arrange
        var headers = new Dictionary<string, string>
        {
            ["BW-TraceparentId"] = "00-abc-def-01",
        };
        var message = new ServiceBusMessage(BinaryData.Empty);

        // Act
        AzureServiceBusHeaderMapper.MapOutbound(headers, message);

        // Assert
        message.ApplicationProperties.Should().ContainKey("BW-TraceparentId");
        message.ApplicationProperties["BW-TraceparentId"].Should().Be("00-abc-def-01");
    }

    // ── MapInbound ───────────────────────────────────────────────────────────

    [Fact]
    public void MapInbound_WithNullProperties_ReturnsEmptyDictionary()
    {
        // Act
        Dictionary<string, string> result = AzureServiceBusHeaderMapper.MapInbound(null);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public void MapInbound_WithEmptyProperties_ReturnsEmptyDictionary()
    {
        // Arrange
        var props = new Dictionary<string, object>();

        // Act
        Dictionary<string, string> result = AzureServiceBusHeaderMapper.MapInbound(props);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void MapInbound_WithStringValue_MapsDirectly()
    {
        // Arrange
        var props = new Dictionary<string, object>
        {
            ["message-id"] = "test-id-42",
        };

        // Act
        Dictionary<string, string> result = AzureServiceBusHeaderMapper.MapInbound(props);

        // Assert
        result.Should().ContainKey("message-id");
        result["message-id"].Should().Be("test-id-42");
    }

    [Fact]
    public void MapInbound_WithNonStringValue_ConvertsToString()
    {
        // Arrange — ASB ApplicationProperties supports object values (int, bool, long, etc.)
        var props = new Dictionary<string, object>
        {
            ["retry-count"] = 3,
            ["is-retry"] = true,
        };

        // Act
        Dictionary<string, string> result = AzureServiceBusHeaderMapper.MapInbound(props);

        // Assert
        result["retry-count"].Should().Be("3");
        result["is-retry"].Should().Be("True");
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_OutboundThenInbound_PreservesKeysAndValues()
    {
        // Arrange
        var original = new Dictionary<string, string>
        {
            ["message-id"] = "round-trip-id",
            ["traceparent"] = "00-abc123-def456-01",
            ["BW-Queue"] = "my-queue",
        };
        var message = new ServiceBusMessage(BinaryData.Empty);

        // Act — outbound
        AzureServiceBusHeaderMapper.MapOutbound(original, message);

        // Build an inbound-style dictionary from the application properties.
        var inboundProps = message.ApplicationProperties
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        Dictionary<string, string> restored = AzureServiceBusHeaderMapper.MapInbound(inboundProps);

        // Assert
        restored.Should().HaveCount(original.Count);
        foreach (KeyValuePair<string, string> entry in original)
        {
            restored.Should().ContainKey(entry.Key);
            restored[entry.Key].Should().Be(entry.Value);
        }
    }

    [Fact]
    public void MapInbound_UseOrdinalStringComparer()
    {
        // Arrange — verify that the dictionary uses Ordinal comparer (case-sensitive).
        var props = new Dictionary<string, object>
        {
            ["Key"] = "upper",
            ["key"] = "lower",
        };

        // Act
        Dictionary<string, string> result = AzureServiceBusHeaderMapper.MapInbound(props);

        // Assert — both keys retained because Ordinal comparison treats them as distinct.
        result.Should().HaveCount(2);
        result["Key"].Should().Be("upper");
        result["key"].Should().Be("lower");
    }
}
