using System.Text;
using AwesomeAssertions;
using BareWire.Transport.Kafka;
using Confluent.Kafka;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaHeaderMapperTests
{
    // ── MapOutbound ──────────────────────────────────────────────────────────

    [Fact]
    public void MapOutbound_EmptyDictionary_ReturnsEmptyHeaders()
    {
        // Arrange
        var headers = new Dictionary<string, string>();

        // Act
        Headers result = KafkaHeaderMapper.MapOutbound(headers);

        // Assert
        result.Should().NotBeNull();
        result.Count.Should().Be(0);
    }

    [Fact]
    public void MapOutbound_SingleHeader_IsEncoded()
    {
        // Arrange
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json"
        };

        // Act
        Headers result = KafkaHeaderMapper.MapOutbound(headers);

        // Assert
        result.Count.Should().Be(1);
        IHeader header = result[0];
        header.Key.Should().Be("content-type");
        Encoding.UTF8.GetString(header.GetValueBytes()).Should().Be("application/json");
    }

    [Fact]
    public void MapOutbound_MultipleHeaders_AllEncoded()
    {
        // Arrange
        var headers = new Dictionary<string, string>
        {
            ["message-id"] = "abc-123",
            ["correlation-id"] = "xyz-456",
            ["BW-PartitionKey"] = "order-99",
        };

        // Act
        Headers result = KafkaHeaderMapper.MapOutbound(headers);

        // Assert
        result.Count.Should().Be(3);
        var resultDict = result.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()));
        resultDict["message-id"].Should().Be("abc-123");
        resultDict["correlation-id"].Should().Be("xyz-456");
        resultDict["BW-PartitionKey"].Should().Be("order-99");
    }

    // ── MapInbound ───────────────────────────────────────────────────────────

    [Fact]
    public void MapInbound_NullHeaders_ReturnsEmptyDictionary()
    {
        // Act
        Dictionary<string, string> result = KafkaHeaderMapper.MapInbound(null);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public void MapInbound_EmptyHeaders_ReturnsEmptyDictionary()
    {
        // Arrange
        var headers = new Headers();

        // Act
        Dictionary<string, string> result = KafkaHeaderMapper.MapInbound(headers);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void MapInbound_SingleHeader_IsDecoded()
    {
        // Arrange
        var headers = new Headers();
        headers.Add("content-type", Encoding.UTF8.GetBytes("application/json"));

        // Act
        Dictionary<string, string> result = KafkaHeaderMapper.MapInbound(headers);

        // Assert
        result.Should().ContainKey("content-type");
        result["content-type"].Should().Be("application/json");
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
            ["BW-PartitionKey"] = "user-42",
        };

        // Act
        Headers kafkaHeaders = KafkaHeaderMapper.MapOutbound(original);
        Dictionary<string, string> restored = KafkaHeaderMapper.MapInbound(kafkaHeaders);

        // Assert
        restored.Should().HaveCount(original.Count);
        foreach (KeyValuePair<string, string> entry in original)
        {
            restored.Should().ContainKey(entry.Key);
            restored[entry.Key].Should().Be(entry.Value);
        }
    }

    [Fact]
    public void RoundTrip_UnicodeValues_PreservedCorrectly()
    {
        // Arrange
        var original = new Dictionary<string, string>
        {
            ["x-custom"] = "zażółć gęślą jaźń"
        };

        // Act
        Headers kafkaHeaders = KafkaHeaderMapper.MapOutbound(original);
        Dictionary<string, string> restored = KafkaHeaderMapper.MapInbound(kafkaHeaders);

        // Assert
        restored["x-custom"].Should().Be("zażółć gęślą jaźń");
    }
}
