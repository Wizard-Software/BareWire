using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Interop.MassTransit;

namespace BareWire.UnitTests.Interop;

/// <summary>
/// Unit tests for <see cref="MassTransitEnvelopeDeserializer"/> implementing
/// <see cref="IRequestEnvelopeRouteReader.TryReadRequestEnvelope"/>.
/// </summary>
public sealed class MassTransitEnvelopeRouteReaderTests
{
    private readonly MassTransitEnvelopeDeserializer _sut = new();

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryReadRequestEnvelope_WithAllFields_ReturnsTrueAndPopulatesRouting()
    {
        var requestId = Guid.NewGuid();
        const string responseAddress = "rabbitmq://localhost/responses";
        const string faultAddress = "rabbitmq://localhost/faults";
        var json = $$"""
            {
              "messageId": "{{Guid.NewGuid()}}",
              "requestId": "{{requestId}}",
              "responseAddress": "{{responseAddress}}",
              "faultAddress": "{{faultAddress}}",
              "messageType": ["urn:message:TestRequest"],
              "message": {"id": 1}
            }
            """;
        var body = ToSequence(json);

        bool result = _sut.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing);

        result.Should().BeTrue();
        routing.RequestId.Should().Be(requestId);
        routing.ResponseAddress.Should().Be(responseAddress);
        routing.FaultAddress.Should().Be(faultAddress);
    }

    [Fact]
    public void TryReadRequestEnvelope_WithRequestIdAndResponseAddressOnly_ReturnsTrueAndPopulatesCore()
    {
        var requestId = Guid.NewGuid();
        const string responseAddress = "rabbitmq://localhost/MT_bus_reply";
        var json = $$"""
            {
              "requestId": "{{requestId}}",
              "responseAddress": "{{responseAddress}}",
              "message": {"id": 2}
            }
            """;
        var body = ToSequence(json);

        bool result = _sut.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing);

        result.Should().BeTrue();
        routing.RequestId.Should().Be(requestId);
        routing.ResponseAddress.Should().Be(responseAddress);
        routing.FaultAddress.Should().BeNull();
    }

    [Fact]
    public void TryReadRequestEnvelope_WithNestedObjectsBeforeRequestId_StillFindsRequestId()
    {
        var requestId = Guid.NewGuid();
        var json = $$"""
            {
              "messageId": "{{Guid.NewGuid()}}",
              "headers": {"x-custom": "value", "x-other": "other"},
              "requestId": "{{requestId}}",
              "responseAddress": "rabbitmq://localhost/q",
              "message": {"id": 3}
            }
            """;
        var body = ToSequence(json);

        bool result = _sut.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing);

        result.Should().BeTrue();
        routing.RequestId.Should().Be(requestId);
    }

    // ── Missing fields → false ──────────────────────────────────────────────────

    [Fact]
    public void TryReadRequestEnvelope_WithMissingRequestId_ReturnsFalse()
    {
        var json = """{"responseAddress":"rabbitmq://localhost/q","message":{"id":4}}""";
        var body = ToSequence(json);

        bool result = _sut.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing);

        result.Should().BeFalse();
        routing.RequestId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryReadRequestEnvelope_WithEmptyBody_ReturnsFalse()
    {
        bool result = _sut.TryReadRequestEnvelope(ReadOnlySequence<byte>.Empty, out RequestEnvelopeContext routing);

        result.Should().BeFalse();
        routing.RequestId.Should().Be(Guid.Empty);
    }

    // ── Error resilience — never throws ────────────────────────────────────────

    [Fact]
    public void TryReadRequestEnvelope_WithMalformedJson_ReturnsFalseAndNeverThrows()
    {
        var body = ToSequence("not valid json {{{{");

        Action act = () => _sut.TryReadRequestEnvelope(body, out _);

        act.Should().NotThrow();

        bool result = _sut.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing);
        result.Should().BeFalse();
        routing.RequestId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryReadRequestEnvelope_WithInvalidGuidForRequestId_ReturnsFalse()
    {
        var json = """{"requestId":"not-a-guid","responseAddress":"rabbitmq://localhost/q","message":{}}""";
        var body = ToSequence(json);

        bool result = _sut.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing);

        result.Should().BeFalse();
        routing.RequestId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void TryReadRequestEnvelope_WithNonObjectRoot_ReturnsFalse()
    {
        var body = ToSequence("[1, 2, 3]");

        bool result = _sut.TryReadRequestEnvelope(body, out _);

        result.Should().BeFalse();
    }

    // ── SEC-1: responseAddress sanitization ────────────────────────────────────

    [Fact]
    public void TryReadRequestEnvelope_WithCredentialsInResponseAddress_StoresRawAddress()
    {
        // The reader stores the raw address; sanitization happens at RespondAsync call site (SEC-1).
        // This test confirms the reader reads the address field without modification.
        var requestId = Guid.NewGuid();
        const string rawAddress = "rabbitmq://user:pass@broker/vhost/queue";
        var json = $$"""
            {
              "requestId": "{{requestId}}",
              "responseAddress": "{{rawAddress}}",
              "message": {}
            }
            """;
        var body = ToSequence(json);

        bool result = _sut.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing);

        result.Should().BeTrue();
        routing.ResponseAddress.Should().Be(rawAddress);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ReadOnlySequence<byte> ToSequence(string json)
        => new(Encoding.UTF8.GetBytes(json));
}
