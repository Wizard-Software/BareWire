using System.Buffers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Buffers;
using BareWire.Interop.MassTransit;

namespace BareWire.UnitTests.Interop;

public sealed class MassTransitEnvelopeSerializerTests
{
    private readonly MassTransitEnvelopeSerializer _sut = new();

    [Fact]
    public void Serialize_ValidMessage_ProducesValidEnvelope()
    {
        var order = new TestOrder("ORD-1", 99.99m);
        using var buffer = new PooledBufferWriter();

        _sut.Serialize(order, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("messageId").GetGuid().Should().NotBeEmpty();
        root.GetProperty("messageType").GetArrayLength().Should().Be(1);
        root.GetProperty("sentTime").GetDateTimeOffset().Should()
            .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        root.GetProperty("message").GetProperty("orderId").GetString().Should().Be("ORD-1");
    }

    [Fact]
    public void ContentType_ReturnsVndMasstransitJson()
    {
        _sut.ContentType.Should().Be("application/vnd.masstransit+json");
    }

    [Fact]
    public void Serialize_EmitsMessageIdAsGuid()
    {
        var order = new TestOrder("ORD-2", 1.00m);
        using var buffer = new PooledBufferWriter();

        _sut.Serialize(order, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var messageId = doc.RootElement.GetProperty("messageId").GetGuid();

        messageId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Serialize_EmitsMessageTypeAsUrn()
    {
        var order = new TestOrder("ORD-3", 2.00m);
        using var buffer = new PooledBufferWriter();

        _sut.Serialize(order, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var messageType = doc.RootElement.GetProperty("messageType");
        var firstEntry = messageType[0].GetString()!;

        firstEntry.Should().StartWith("urn:message:");
        firstEntry.Should().Contain(typeof(TestOrder).Namespace!);
        firstEntry.Should().Contain(nameof(TestOrder));
    }

    [Fact]
    public void Serialize_EmitsSentTimeUtc()
    {
        var order = new TestOrder("ORD-4", 3.00m);
        using var buffer = new PooledBufferWriter();

        _sut.Serialize(order, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var sentTime = doc.RootElement.GetProperty("sentTime").GetDateTimeOffset();

        sentTime.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Serialize_NullMessage_ThrowsArgumentNull()
    {
        using var buffer = new PooledBufferWriter();

        Assert.Throws<ArgumentNullException>(() => _sut.Serialize<TestOrder>(null!, buffer));
    }

    [Fact]
    public void Serialize_NullOutput_ThrowsArgumentNull()
    {
        var order = new TestOrder("ORD-5", 4.00m);

        Assert.Throws<ArgumentNullException>(() => _sut.Serialize(order, null!));
    }

    [Fact]
    public void Serialize_NestedMessage_PreservesNestedData()
    {
        var inner = new InnerTestData(42, "blue");
        var nested = new NestedTestOrder("Widget", inner);
        using var buffer = new PooledBufferWriter();

        _sut.Serialize(nested, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var message = doc.RootElement.GetProperty("message");

        message.GetProperty("name").GetString().Should().Be("Widget");
        message.GetProperty("inner").GetProperty("id").GetInt32().Should().Be(42);
        message.GetProperty("inner").GetProperty("description").GetString().Should().Be("blue");
    }

    // ── IRequestEnvelopeSerializer tests (RED → GREEN in step 4) ──────────────

    [Fact]
    public void SerializeWithContext_WritesResponseAddressRequestIdDestinationAndExpiration()
    {
        // Arrange
        var requestEnvelopeSerializer = (IRequestEnvelopeSerializer)_sut;
        var requestId = Guid.NewGuid();
        var expiration = DateTimeOffset.UtcNow.AddSeconds(30);
        var context = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/my-reply-queue",
            DestinationAddress: "rabbitmq://localhost/order-commands",
            FaultAddress: "rabbitmq://localhost/my-reply-queue",
            RequestId: requestId,
            CorrelationId: null,
            ExpirationTime: expiration);
        var order = new TestOrder("ORD-REQ", 50m);
        using var buffer = new PooledBufferWriter();

        // Act
        requestEnvelopeSerializer.Serialize(order, in context, buffer);

        // Assert
        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("responseAddress").GetString().Should().Be("rabbitmq://localhost/my-reply-queue");
        root.GetProperty("requestId").GetGuid().Should().Be(requestId);
        root.GetProperty("destinationAddress").GetString().Should().Be("rabbitmq://localhost/order-commands");
        root.GetProperty("faultAddress").GetString().Should().Be("rabbitmq://localhost/my-reply-queue");
        root.GetProperty("expirationTime").GetDateTimeOffset().Should().BeCloseTo(expiration, TimeSpan.FromSeconds(1));
        root.GetProperty("message").GetProperty("orderId").GetString().Should().Be("ORD-REQ");
    }

    [Fact]
    public void SerializeWithContext_WhenCorrelationIdSet_WritesCorrelationId()
    {
        var requestEnvelopeSerializer = (IRequestEnvelopeSerializer)_sut;
        var correlationId = Guid.NewGuid();
        var context = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/reply",
            DestinationAddress: "rabbitmq://localhost/dest",
            FaultAddress: null,
            RequestId: Guid.NewGuid(),
            CorrelationId: correlationId,
            ExpirationTime: null);
        using var buffer = new PooledBufferWriter();

        requestEnvelopeSerializer.Serialize(new TestOrder("ORD-CORR", 1m), in context, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("correlationId").GetGuid().Should().Be(correlationId);
    }

    /// <summary>
    /// Regression: the IMessageSerializer.Serialize path (no context) must NOT emit
    /// routing fields (responseAddress, requestId, destinationAddress, faultAddress, expirationTime).
    /// </summary>
    [Fact]
    public void Serialize_ViaIMessageSerializer_DoesNotEmitRoutingFields()
    {
        var order = new TestOrder("ORD-REGR", 10m);
        using var buffer = new PooledBufferWriter();

        _sut.Serialize(order, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("responseAddress", out _).Should().BeFalse("plain Serialize must not emit responseAddress");
        root.TryGetProperty("requestId", out _).Should().BeFalse("plain Serialize must not emit requestId");
        root.TryGetProperty("destinationAddress", out _).Should().BeFalse("plain Serialize must not emit destinationAddress");
        root.TryGetProperty("faultAddress", out _).Should().BeFalse("plain Serialize must not emit faultAddress");
        root.TryGetProperty("expirationTime", out _).Should().BeFalse("plain Serialize must not emit expirationTime");
    }
}
