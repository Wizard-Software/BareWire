using System.Buffers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Buffers;
using BareWire.Interop.MassTransit;

namespace BareWire.UnitTests.Interop;

/// <summary>
/// Unit tests for <see cref="MassTransitEnvelopeSerializer"/> implementing
/// <see cref="IResponseEnvelopeWriter.WriteResponse{T}"/>.
/// </summary>
public sealed class MassTransitEnvelopeResponseWriterTests
{
    private readonly MassTransitEnvelopeSerializer _sut = new();

    private sealed record OrderResponse(string Status, decimal Total);

    [Fact]
    public void WriteResponse_ProducesValidMassTransitEnvelope()
    {
        var requestId = Guid.NewGuid();
        var response = new OrderResponse("Completed", 99.99m);
        using var buffer = new PooledBufferWriter();

        ((IResponseEnvelopeWriter)_sut).WriteResponse(response, requestId, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("messageId", out _).Should().BeTrue("messageId must be present");
        root.GetProperty("messageId").GetGuid().Should().NotBeEmpty();
    }

    [Fact]
    public void WriteResponse_EchoesRequestId()
    {
        var requestId = Guid.NewGuid();
        var response = new OrderResponse("Done", 1.00m);
        using var buffer = new PooledBufferWriter();

        ((IResponseEnvelopeWriter)_sut).WriteResponse(response, requestId, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("requestId", out JsonElement requestIdElement).Should().BeTrue("requestId must be echoed");
        requestIdElement.GetGuid().Should().Be(requestId);
    }

    [Fact]
    public void WriteResponse_EmitsMessageTypeUrn()
    {
        var requestId = Guid.NewGuid();
        var response = new OrderResponse("Shipped", 2.50m);
        using var buffer = new PooledBufferWriter();

        ((IResponseEnvelopeWriter)_sut).WriteResponse(response, requestId, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var messageType = doc.RootElement.GetProperty("messageType");

        messageType.GetArrayLength().Should().Be(1);
        string firstEntry = messageType[0].GetString()!;
        firstEntry.Should().StartWith("urn:message:");
        firstEntry.Should().Contain(nameof(OrderResponse));
    }

    [Fact]
    public void WriteResponse_EmitsMessagePayload()
    {
        var requestId = Guid.NewGuid();
        var response = new OrderResponse("Pending", 42.00m);
        using var buffer = new PooledBufferWriter();

        ((IResponseEnvelopeWriter)_sut).WriteResponse(response, requestId, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);
        var message = doc.RootElement.GetProperty("message");

        message.GetProperty("status").GetString().Should().Be("Pending");
        message.GetProperty("total").GetDecimal().Should().Be(42.00m);
    }

    [Fact]
    public void WriteResponse_EmitsSentTime()
    {
        var requestId = Guid.NewGuid();
        using var buffer = new PooledBufferWriter();

        ((IResponseEnvelopeWriter)_sut).WriteResponse(new OrderResponse("Ok", 0m), requestId, buffer);

        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("sentTime").GetDateTimeOffset().Should()
            .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WriteResponse_DifferentRequestIds_ProduceDifferentEnvelopes()
    {
        var requestId1 = Guid.NewGuid();
        var requestId2 = Guid.NewGuid();
        var response = new OrderResponse("Done", 1m);

        using var buffer1 = new PooledBufferWriter();
        using var buffer2 = new PooledBufferWriter();

        ((IResponseEnvelopeWriter)_sut).WriteResponse(response, requestId1, buffer1);
        ((IResponseEnvelopeWriter)_sut).WriteResponse(response, requestId2, buffer2);

        string json1 = Encoding.UTF8.GetString(buffer1.WrittenSpan);
        string json2 = Encoding.UTF8.GetString(buffer2.WrittenSpan);

        using var doc1 = JsonDocument.Parse(json1);
        using var doc2 = JsonDocument.Parse(json2);

        doc1.RootElement.GetProperty("requestId").GetGuid().Should().Be(requestId1);
        doc2.RootElement.GetProperty("requestId").GetGuid().Should().Be(requestId2);
        doc1.RootElement.GetProperty("requestId").GetGuid().Should()
            .NotBe(doc2.RootElement.GetProperty("requestId").GetGuid());
    }
}
