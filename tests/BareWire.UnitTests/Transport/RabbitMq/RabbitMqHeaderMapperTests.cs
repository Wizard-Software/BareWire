using AwesomeAssertions;
using NSubstitute;
using RabbitMQ.Client;
using BareWire.Transport.RabbitMQ;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqHeaderMapperTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyBasicProperties CreateProperties(
        string? messageId = null,
        string? correlationId = null,
        string? contentType = null,
        string? type = null,
        IDictionary<string, object?>? headers = null)
    {
        var props = Substitute.For<IReadOnlyBasicProperties>();
        props.MessageId.Returns(messageId ?? string.Empty);
        props.CorrelationId.Returns(correlationId ?? string.Empty);
        props.ContentType.Returns(contentType ?? string.Empty);
        props.Type.Returns(type ?? string.Empty);
        props.Headers.Returns(headers);
        return props;
    }

    private static RabbitMqHeaderMapper CreateDefaultMapper() =>
        new();

    private static RabbitMqHeaderMapper CreateMapperWith(
        Action<RabbitMqHeaderMappingConfigurator> configure)
    {
        var config = new RabbitMqHeaderMappingConfigurator();
        configure(config);
        return new RabbitMqHeaderMapper(config);
    }

    // ── MapInbound — default mappings ─────────────────────────────────────────

    [Fact]
    public void MapInbound_DefaultMappings_MapsStandardProperties()
    {
        // Arrange
        var sut = CreateDefaultMapper();
        IReadOnlyBasicProperties properties = CreateProperties(
            messageId: "msg-001",
            correlationId: "corr-abc",
            contentType: "application/json",
            type: "OrderCreated");

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["message-id"].Should().Be("msg-001");
        result["correlation-id"].Should().Be("corr-abc");
        result["content-type"].Should().Be("application/json");
        result["BW-MessageType"].Should().Be("OrderCreated");
    }

    [Fact]
    public void MapInbound_CustomMapping_OverridesDefault()
    {
        // Arrange — correlation-id should be read from AMQP Header "X-Correlation-ID"
        var amqpHeaders = new Dictionary<string, object?> { ["X-Correlation-ID"] = "custom-corr-99" };
        IReadOnlyBasicProperties properties = CreateProperties(
            correlationId: "should-be-ignored",
            headers: amqpHeaders);

        var sut = CreateMapperWith(cfg => cfg.MapCorrelationId("X-Correlation-ID"));

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["correlation-id"].Should().Be("custom-corr-99");
    }

    [Fact]
    public void MapInbound_IgnoreUnmappedHeaders_DropsUnknown()
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?> { ["x-custom-header"] = "should-be-dropped" };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);

        var sut = CreateMapperWith(cfg => cfg.IgnoreUnmappedHeaders(true));

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result.Should().NotContainKey("x-custom-header");
    }

    [Fact]
    public void MapInbound_WithMtActivityIdAndIgnoreUnmapped_MapsMtActivityId()
    {
        // Arrange
        const string traceValue = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var amqpHeaders = new Dictionary<string, object?>
        {
            ["MT-Activity-Id"] = traceValue,
            ["x-other"] = "dropped",
        };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);
        var sut = CreateMapperWith(cfg => cfg.IgnoreUnmappedHeaders(true));

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["MT-Activity-Id"].Should().Be(traceValue);
        result.Should().NotContainKey("x-other");
    }

    [Fact]
    public void MapOutbound_WithTraceHeadersAndIgnoreUnmapped_SendsTraceHeaders()
    {
        // Arrange
        const string traceValue = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = traceValue,
            ["tracestate"] = "vendor=1",
            ["baggage"] = "k=v",
            ["MT-Activity-Id"] = traceValue,
            ["x-other"] = "dropped",
        };
        var sut = CreateMapperWith(cfg => cfg.IgnoreUnmappedHeaders(true));

        // Act
        (_, Dictionary<string, object?> amqpHeaders) = sut.MapOutbound(headers);

        // Assert
        amqpHeaders["traceparent"].Should().Be(traceValue);
        amqpHeaders["tracestate"].Should().Be("vendor=1");
        amqpHeaders["baggage"].Should().Be("k=v");
        amqpHeaders["MT-Activity-Id"].Should().Be(traceValue);
        amqpHeaders.Should().NotContainKey("x-other");
    }

    [Fact]
    public void MapInbound_WithTracestateAndBaggageAndIgnoreUnmapped_MapsThem()
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["TraceState"] = "vendor=1",
            ["baggage"] = "k=v",
            ["x-other"] = "dropped",
        };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);
        var sut = CreateMapperWith(cfg => cfg.IgnoreUnmappedHeaders(true));

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["tracestate"].Should().Be("vendor=1");
        result["baggage"].Should().Be("k=v");
        result.Should().NotContainKey("x-other");
    }

    [Fact]
    public void MapOutbound_WithExplicitTracestateMapping_UsesMappedName()
    {
        // Arrange
        var headers = new Dictionary<string, string> { ["tracestate"] = "vendor=1" };
        var sut = CreateMapperWith(cfg => cfg.MapHeader("tracestate", "x-tracestate"));

        // Act
        (_, Dictionary<string, object?> amqpHeaders) = sut.MapOutbound(headers);

        // Assert
        amqpHeaders["x-tracestate"].Should().Be("vendor=1");
        amqpHeaders.Should().NotContainKey("tracestate");
    }

    [Fact]
    public void MapInbound_WithDifferentlyCasedMtActivityId_WritesNormalizedKey()
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mt-activity-id"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);

        // Act
        Dictionary<string, string> result = CreateDefaultMapper().MapInbound(properties);

        // Assert
        result.Should().ContainKey("MT-Activity-Id");
        result.Should().NotContainKey("mt-activity-id");
    }

    [Fact]
    public void MapInbound_WithExplicitCustomMappingOfMtActivityId_CustomMappingWins()
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?> { ["MT-Activity-Id"] = "custom-value" };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);
        var sut = CreateMapperWith(cfg => cfg.MapHeader("X-Trace", "MT-Activity-Id"));

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["X-Trace"].Should().Be("custom-value");
        result.Should().NotContainKey("MT-Activity-Id");
    }

    [Fact]
    public void MapOutbound_WithMtActivityId_PassesThroughUnprefixed()
    {
        // Arrange
        var headers = new Dictionary<string, string> { ["MT-Activity-Id"] = "00-aa-bb-01" };

        // Act
        (BasicProperties _, Dictionary<string, object?> amqpHeaders) = CreateDefaultMapper().MapOutbound(headers);

        // Assert
        amqpHeaders["MT-Activity-Id"].Should().Be("00-aa-bb-01");
    }

    [Fact]
    public void MapInbound_WithoutIgnoreUnmapped_PassesAllHeaders()
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?> { ["x-tenant-id"] = "tenant-42" };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);

        var sut = CreateDefaultMapper(); // IgnoreUnmapped defaults to false

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["x-tenant-id"].Should().Be("tenant-42");
    }

    [Fact]
    public void MapInbound_MissingOptionalHeaders_ReturnsPartialMap()
    {
        // Arrange — only MessageId set, CorrelationId absent
        IReadOnlyBasicProperties properties = CreateProperties(messageId: "msg-only");

        var sut = CreateDefaultMapper();

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["message-id"].Should().Be("msg-only");
        result.Should().NotContainKey("correlation-id");
        result.Should().NotContainKey("content-type");
    }

    [Fact]
    public void MapInbound_TraceparentHeader_Preserved()
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?>
        {
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);

        var sut = CreateDefaultMapper();

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["traceparent"].Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    }

    // ── Reserved BW-* header trust boundary ─────────────────────────────────────

    [Theory]
    [InlineData("bw-forged")]
    [InlineData("Bw-Forged")]
    [InlineData("BW-FORGED")]
    public void MapOutbound_BwPrefixedHeaderInAnyCaseWithoutMapping_IsNotSentOnTheWire(string forgedKey)
    {
        // Arrange
        var sut = CreateDefaultMapper();
        var headers = new Dictionary<string, string>
        {
            [forgedKey] = "forged-value",
            ["x-tenant-id"] = "tenant-42",
        };

        // Act
        (_, Dictionary<string, object?> amqpHeaders) = sut.MapOutbound(headers);

        // Assert
        amqpHeaders.Should().NotContainKey(forgedKey);
        amqpHeaders.Should().ContainKey("x-tenant-id", "non-reserved headers must still pass through");
    }

    [Theory]
    [InlineData("BW-Exchange")]
    [InlineData("bw-exchange")]
    [InlineData("BW-RoutingKey")]
    [InlineData("Bw-ConsumerChannelId")]
    [InlineData("BW-Forged")]
    [InlineData("bw-messagetype")]
    [InlineData("BW-Endpoint")]
    [InlineData("bw-mappingepoch")]
    public void MapInbound_UnmappedRawBwHeaderInAnyCase_IsDropped(string forgedKey)
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?>
        {
            [forgedKey] = "forged-value",
            ["x-tenant-id"] = "tenant-42",
        };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);
        var sut = CreateDefaultMapper();

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result.Should().NotContainKey(forgedKey);
        result["x-tenant-id"].Should().Be("tenant-42", "non-reserved headers must still pass through");
    }

    [Fact]
    public void MapInbound_RawBwMessageTypeHeader_DoesNotOverrideAmqpTypeProperty()
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?> { ["BW-MessageType"] = "ForgedType" };
        IReadOnlyBasicProperties properties = CreateProperties(type: "OrderCreated", headers: amqpHeaders);
        var sut = CreateDefaultMapper();

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["BW-MessageType"].Should().Be("OrderCreated");
    }

    [Fact]
    public void MapInbound_RawBwMessageTypeHeaderWhenAmqpTypeIsEmpty_IsAccepted()
    {
        // Arrange — no AMQP Type property set, so the raw BW-MessageType header is the only source.
        var amqpHeaders = new Dictionary<string, object?> { ["BW-MessageType"] = "OrderCreated" };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);
        var sut = CreateDefaultMapper();

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result["BW-MessageType"].Should().Be("OrderCreated");
    }

    [Fact]
    public void MapInbound_TransportStampedMappingEpochHeader_IsPreserved()
    {
        // Arrange
        var amqpHeaders = new Dictionary<string, object?>
        {
            [RabbitMqTransportAdapter.MappingEpochHeaderName] = 42L,
        };
        IReadOnlyBasicProperties properties = CreateProperties(headers: amqpHeaders);
        var sut = CreateDefaultMapper();

        // Act
        Dictionary<string, string> result = sut.MapInbound(properties);

        // Assert
        result[RabbitMqTransportAdapter.MappingEpochHeaderName].Should().Be("42");
    }

    [Fact]
    public void MapInbound_ExplicitIdentityMappedBwHeader_IsPreservedInBothDirections()
    {
        // Arrange
        var sut = CreateMapperWith(cfg => cfg.MapHeader("BW-TenantId", "BW-TenantId"));
        IReadOnlyBasicProperties properties = CreateProperties(
            headers: new Dictionary<string, object?> { ["BW-TenantId"] = "tenant-7" });

        // Act
        (_, Dictionary<string, object?> outbound) =
            sut.MapOutbound(new Dictionary<string, string> { ["BW-TenantId"] = "tenant-7" });
        Dictionary<string, string> inbound = sut.MapInbound(properties);

        // Assert
        outbound["BW-TenantId"].Should().Be("tenant-7");
        inbound["BW-TenantId"].Should().Be("tenant-7");
    }

    // ── MapOutbound — default mappings ────────────────────────────────────────

    [Fact]
    public void MapOutbound_DefaultMappings_SetsAmqpProperties()
    {
        // Arrange
        var bareWireHeaders = new Dictionary<string, string>
        {
            ["message-id"] = "out-001",
            ["correlation-id"] = "corr-xyz",
            ["content-type"] = "application/json",
            ["BW-MessageType"] = "OrderCreated",
        };

        var sut = CreateDefaultMapper();

        // Act
        (BasicProperties props, Dictionary<string, object?> headers) = sut.MapOutbound(bareWireHeaders);

        // Assert — standard headers map to AMQP properties
        props.MessageId.Should().Be("out-001");
        props.CorrelationId.Should().Be("corr-xyz");
        props.ContentType.Should().Be("application/json");
        props.Type.Should().Be("OrderCreated");
        headers.Should().BeEmpty();
    }

    [Fact]
    public void MapOutbound_CustomHeaders_GoToHeadersDictionary()
    {
        // Arrange
        var bareWireHeaders = new Dictionary<string, string>
        {
            ["x-tenant-id"] = "tenant-77",
            ["x-request-id"] = "req-abc",
        };

        var sut = CreateDefaultMapper();

        // Act
        (BasicProperties _, Dictionary<string, object?> headers) = sut.MapOutbound(bareWireHeaders);

        // Assert — non-standard headers go to the AMQP headers dictionary
        headers["x-tenant-id"].Should().Be("tenant-77");
        headers["x-request-id"].Should().Be("req-abc");
    }

    // ── MapHeader — bidirectional custom mapping ───────────────────────────────

    [Fact]
    public void MapHeader_WithTransformation_AppliesMapping()
    {
        // Arrange
        var sut = CreateMapperWith(cfg => cfg.MapHeader("BW-TenantId", "X-TenantId"));

        // Outbound: BW-TenantId → X-TenantId
        var outboundHeaders = new Dictionary<string, string> { ["BW-TenantId"] = "tenant-99" };
        (BasicProperties _, Dictionary<string, object?> amqpHeaders) = sut.MapOutbound(outboundHeaders);
        amqpHeaders["X-TenantId"].Should().Be("tenant-99");
        amqpHeaders.Should().NotContainKey("BW-TenantId");

        // Inbound: X-TenantId → BW-TenantId
        var inboundAmqpHeaders = new Dictionary<string, object?> { ["X-TenantId"] = "tenant-99" };
        IReadOnlyBasicProperties props = CreateProperties(headers: inboundAmqpHeaders);
        Dictionary<string, string> inbound = sut.MapInbound(props);
        inbound["BW-TenantId"].Should().Be("tenant-99");
        inbound.Should().NotContainKey("X-TenantId");
    }

    // ── ReplyTo mapping ───────────────────────────────────────────────────────

    [Fact]
    public void MapInbound_WithReplyTo_IncludesReplyToHeader()
    {
        // Arrange
        var props = Substitute.For<IReadOnlyBasicProperties>();
        props.MessageId.Returns(string.Empty);
        props.CorrelationId.Returns(string.Empty);
        props.ContentType.Returns(string.Empty);
        props.Type.Returns(string.Empty);
        props.ReplyTo.Returns("amq.gen-test-queue");
        props.Headers.Returns((IDictionary<string, object?>?)null);

        var sut = CreateDefaultMapper();

        // Act
        Dictionary<string, string> result = sut.MapInbound(props);

        // Assert
        result.Should().ContainKey("ReplyTo");
        result["ReplyTo"].Should().Be("amq.gen-test-queue");
    }

    [Fact]
    public void MapInbound_WithoutReplyTo_DoesNotIncludeReplyToHeader()
    {
        // Arrange — ReplyTo is null/empty (default from CreateProperties)
        IReadOnlyBasicProperties props = CreateProperties(messageId: "msg-001");

        var sut = CreateDefaultMapper();

        // Act
        Dictionary<string, string> result = sut.MapInbound(props);

        // Assert
        result.Should().NotContainKey("ReplyTo");
    }

    // ── Null guard ────────────────────────────────────────────────────────────

    [Fact]
    public void MapInbound_NullProperties_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreateDefaultMapper();

        // Act
        Action act = () => sut.MapInbound(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("properties");
    }

    [Fact]
    public void MapOutbound_NullHeaders_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreateDefaultMapper();

        // Act
        Action act = () => sut.MapOutbound(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>().WithParameterName("bareWireHeaders");
    }
}
