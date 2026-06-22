using System.Buffers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NSubstitute;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Interop.MassTransit;
using BareWire.UnitTests.Abstractions;

namespace BareWire.UnitTests.Interop;

/// <summary>
/// Regression tests for the growable-buffer fix in <see cref="ConsumeContext.RespondAsync{T}"/>
/// when the MT response envelope exceeds 4 096 bytes.
///
/// Before the fix, <c>SingleArrayBufferWriter</c> used a fixed, non-growable 4 096-byte rented
/// array.  Any response whose serialized MT envelope exceeded that size caused
/// <see cref="Utf8JsonWriter"/> to throw <see cref="InvalidOperationException"/> ("could not
/// provide an output buffer large enough"), and the reply was never sent — the MassTransit
/// request client would time out.  These tests must FAIL before the grow fix and PASS after.
/// </summary>
public sealed class ConsumeContextRespondLargeEnvelopeTests
{
    // A response record whose serialized MT envelope reliably exceeds 4 096 bytes.
    // 500 items × ~12 bytes each ≈ 6 000 bytes for the array alone, plus envelope overhead.
    private sealed record LargeResponse(string[] Items, string Description);

    private static LargeResponse BuildLargeResponse()
    {
        string[] items = Enumerable.Range(0, 500).Select(i => $"item-{i:D4}").ToArray();
        string description = new string('x', 1024); // 1 KB description to guarantee overflow
        return new LargeResponse(items, description);
    }

    private static RespondTestableConsumeContext CreateContextWithMtRouting(
        RequestEnvelopeContext inboundRouting,
        IResponseEnvelopeWriter responseWriter,
        ISendEndpointProvider sendEndpointProvider,
        IPublishEndpoint? publishEndpoint = null)
    {
        var ctx = new RespondTestableConsumeContext(
            Guid.NewGuid(),
            correlationId: null,
            conversationId: null,
            sourceAddress: null,
            destinationAddress: null,
            sentTime: null,
            headers: new Dictionary<string, string>(),
            contentType: "application/vnd.masstransit+json",
            rawBody: default,
            publishEndpoint: publishEndpoint ?? Substitute.For<IPublishEndpoint>(),
            sendEndpointProvider: sendEndpointProvider);

        ctx.InboundRequestContext = inboundRouting;
        ctx.ResponseEnvelopeWriter = responseWriter;
        return ctx;
    }

    /// <summary>
    /// When the serialized MT response envelope is larger than 4 096 bytes,
    /// <see cref="ConsumeContext.RespondAsync{T}"/> must NOT throw and must deliver the payload
    /// to <see cref="ISendEndpoint.SendRawAsync"/>.
    ///
    /// This test uses the REAL <see cref="MassTransitEnvelopeSerializer"/> (not a stub) so that
    /// <see cref="Utf8JsonWriter"/> actually exercises the buffer-writer path.
    /// Before the fix this test fails with <see cref="InvalidOperationException"/> thrown by
    /// <see cref="Utf8JsonWriter"/> when the fixed 4 096-byte span is exhausted.
    /// After the fix the buffer grows and the call completes successfully.
    /// </summary>
    [Fact]
    public async Task RespondAsync_WhenEnvelopeExceeds4KiB_DoesNotThrowAndDeliversPayload()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/mt-reply-large",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: requestId,
            CorrelationId: null,
            ExpirationTime: null);

        // Real serializer — exercises the actual Utf8JsonWriter buffer pressure.
        IResponseEnvelopeWriter realWriter = new MassTransitEnvelopeSerializer();

        ReadOnlyMemory<byte> capturedPayload = default;
        ISendEndpoint sendEndpoint = Substitute.For<ISendEndpoint>();
        sendEndpoint
            .SendRawAsync(
                Arg.Do<ReadOnlyMemory<byte>>(p => capturedPayload = p),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();
        sendEndpointProvider
            .GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sendEndpoint));

        var ctx = CreateContextWithMtRouting(inboundRouting, realWriter, sendEndpointProvider);

        LargeResponse response = BuildLargeResponse();

        // Act — must not throw (pre-fix: InvalidOperationException from Utf8JsonWriter)
        await ctx.RespondAsync(response, CancellationToken.None);

        // Assert: reply was delivered
        await sendEndpoint.Received(1)
            .SendRawAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        capturedPayload.IsEmpty.Should().BeFalse("envelope must contain bytes");
        capturedPayload.Length.Should().BeGreaterThan(4096, "envelope must exceed the initial 4 KiB buffer");

        // Structural validation: parse as JSON and check requestId is echoed.
        string json = Encoding.UTF8.GetString(capturedPayload.Span);
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("requestId").GetGuid().Should().Be(requestId);
    }

    /// <summary>
    /// Confirms that a response envelope at exactly 4 096 bytes or smaller still works
    /// (no regression for the common case).
    /// </summary>
    [Fact]
    public async Task RespondAsync_WhenEnvelopeFitsIn4KiB_DeliversPayload()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/mt-reply-small",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: requestId,
            CorrelationId: null,
            ExpirationTime: null);

        IResponseEnvelopeWriter realWriter = new MassTransitEnvelopeSerializer();

        ReadOnlyMemory<byte> capturedPayload = default;
        ISendEndpoint sendEndpoint = Substitute.For<ISendEndpoint>();
        sendEndpoint
            .SendRawAsync(
                Arg.Do<ReadOnlyMemory<byte>>(p => capturedPayload = p),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();
        sendEndpointProvider
            .GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sendEndpoint));

        // Tiny response — well under 4 KiB.
        var ctx = CreateContextWithMtRouting(inboundRouting, realWriter, sendEndpointProvider);

        // Act
        await ctx.RespondAsync(new SimpleResponse("ok"), CancellationToken.None);

        // Assert
        await sendEndpoint.Received(1)
            .SendRawAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        capturedPayload.IsEmpty.Should().BeFalse();
        capturedPayload.Span[0].Should().Be((byte)'{');
    }

    private sealed record SimpleResponse(string Value);
}
