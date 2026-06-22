using System.Buffers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NSubstitute;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;

namespace BareWire.UnitTests.Abstractions;

/// <summary>
/// Tests for the MassTransit branch of <see cref="ConsumeContext.RespondAsync{T}"/>:
/// when the context carries an inbound <see cref="RequestEnvelopeContext"/> (requestId set,
/// no AMQP ReplyTo header), the response must be sent to the reply address — not broadcast
/// via PublishAsync — and the envelope must echo the requestId.
/// </summary>
public sealed class ConsumeContextRespondMtBranchTests
{
    private sealed record ResponseMsg(string Value);

    // A stub IResponseEnvelopeWriter that records the requestId it was called with
    // and writes a minimal JSON envelope to the buffer.
    private sealed class CapturingResponseWriter : IResponseEnvelopeWriter
    {
        public Guid CapturedRequestId { get; private set; }
        public int CallCount { get; private set; }

        public void WriteResponse<T>(T response, Guid requestId, IBufferWriter<byte> output)
            where T : class
        {
            CapturedRequestId = requestId;
            CallCount++;
            // Write minimal valid JSON so SendRawAsync receives non-empty bytes.
            ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes("{\"requestId\":\"" + requestId.ToString() + "\",\"message\":{}}");
            output.Write(bytes);
        }
    }

    private static RespondTestableConsumeContext CreateContextWithMtRouting(
        RequestEnvelopeContext inboundRouting,
        IResponseEnvelopeWriter responseWriter,
        IReadOnlyDictionary<string, string>? headers = null,
        IPublishEndpoint? publishEndpoint = null,
        ISendEndpointProvider? sendEndpointProvider = null)
    {
        var ctx = new RespondTestableConsumeContext(
            Guid.NewGuid(),
            correlationId: null,
            conversationId: null,
            sourceAddress: null,
            destinationAddress: null,
            sentTime: null,
            headers: headers ?? new Dictionary<string, string>(),
            contentType: "application/vnd.masstransit+json",
            rawBody: default,
            publishEndpoint: publishEndpoint ?? Substitute.For<IPublishEndpoint>(),
            sendEndpointProvider: sendEndpointProvider ?? Substitute.For<ISendEndpointProvider>());

        ctx.InboundRequestContext = inboundRouting;
        ctx.ResponseEnvelopeWriter = responseWriter;
        return ctx;
    }

    // ── Happy path: MT branch routes to responseAddress ─────────────────────────

    [Fact]
    public async Task RespondAsync_WithMtRouting_CallsGetSendEndpointWithQueueUri()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/MT_bus_reply",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: requestId,
            CorrelationId: null,
            ExpirationTime: null);

        var writer = new CapturingResponseWriter();

        ISendEndpoint sendEndpoint = Substitute.For<ISendEndpoint>();
        sendEndpoint.SendRawAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();
        sendEndpointProvider
            .GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sendEndpoint));

        IPublishEndpoint publishEndpoint = Substitute.For<IPublishEndpoint>();

        var ctx = CreateContextWithMtRouting(inboundRouting, writer,
            sendEndpointProvider: sendEndpointProvider,
            publishEndpoint: publishEndpoint);

        // Act
        await ctx.RespondAsync(new ResponseMsg("ok"), CancellationToken.None);

        // Assert: sends to an endpoint, never broadcasts
        await sendEndpointProvider.Received(1)
            .GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>());
        await sendEndpoint.Received(1)
            .SendRawAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await publishEndpoint.DidNotReceive().PublishAsync(Arg.Any<ResponseMsg>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RespondAsync_WithMtRouting_EchoesRequestId()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/MT_bus_reply",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: requestId,
            CorrelationId: null,
            ExpirationTime: null);

        var writer = new CapturingResponseWriter();

        ISendEndpoint sendEndpoint = Substitute.For<ISendEndpoint>();
        sendEndpoint.SendRawAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();
        sendEndpointProvider
            .GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sendEndpoint));

        var ctx = CreateContextWithMtRouting(inboundRouting, writer, sendEndpointProvider: sendEndpointProvider);

        // Act
        await ctx.RespondAsync(new ResponseMsg("result"), CancellationToken.None);

        // Assert: writer was called once with the correct requestId
        writer.CallCount.Should().Be(1);
        writer.CapturedRequestId.Should().Be(requestId);
    }

    [Fact]
    public async Task RespondAsync_WithMtRouting_SendsContentTypeMassTransitJson()
    {
        // Arrange
        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/reply",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: Guid.NewGuid(),
            CorrelationId: null,
            ExpirationTime: null);

        string? capturedContentType = null;
        ISendEndpoint sendEndpoint = Substitute.For<ISendEndpoint>();
        sendEndpoint
            .SendRawAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Do<string>(ct => capturedContentType = ct), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();
        sendEndpointProvider.GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
                            .Returns(Task.FromResult(sendEndpoint));

        var ctx = CreateContextWithMtRouting(inboundRouting, new CapturingResponseWriter(),
            sendEndpointProvider: sendEndpointProvider);

        // Act
        await ctx.RespondAsync(new ResponseMsg("typed"), CancellationToken.None);

        // Assert
        capturedContentType.Should().Be("application/vnd.masstransit+json");
    }

    // ── SEC-1: sanitize responseAddress — strip host, take queue name only ──────

    [Fact]
    public async Task RespondAsync_WithResponseAddressContainingCredentials_StripsUserInfo()
    {
        // Arrange — responseAddress with user:pass@host; SEC-1 requires sanitization.
        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://user:secret@broker.internal/vhost/my-reply-queue",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: Guid.NewGuid(),
            CorrelationId: null,
            ExpirationTime: null);

        Uri? capturedUri = null;
        ISendEndpoint sendEndpoint = Substitute.For<ISendEndpoint>();
        sendEndpoint.SendRawAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();
        sendEndpointProvider.GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
                            .Returns(callInfo =>
                            {
                                capturedUri = callInfo.ArgAt<Uri>(0);
                                return Task.FromResult(sendEndpoint);
                            });

        var ctx = CreateContextWithMtRouting(inboundRouting, new CapturingResponseWriter(),
            sendEndpointProvider: sendEndpointProvider);

        // Act
        await ctx.RespondAsync(new ResponseMsg("sec"), CancellationToken.None);

        // Assert: URI must not contain credentials; must route to queue name only
        capturedUri.Should().NotBeNull();
        capturedUri!.UserInfo.Should().BeEmpty();
        capturedUri.AbsolutePath.TrimStart('/').Should().Be("my-reply-queue");
    }

    [Fact]
    public async Task RespondAsync_WithNonRabbitMqScheme_FallsBackToPublishAsync()
    {
        // Arrange — SEC-1: only rabbitmq:// scheme is trusted
        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "amqp://broker/queue",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: Guid.NewGuid(),
            CorrelationId: null,
            ExpirationTime: null);

        IPublishEndpoint publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.PublishAsync(Arg.Any<ResponseMsg>(), Arg.Any<CancellationToken>())
                       .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();

        var ctx = CreateContextWithMtRouting(inboundRouting, new CapturingResponseWriter(),
            publishEndpoint: publishEndpoint,
            sendEndpointProvider: sendEndpointProvider);

        // Act
        await ctx.RespondAsync(new ResponseMsg("fallback"), CancellationToken.None);

        // Assert: falls back to PublishAsync when scheme is not rabbitmq
        await publishEndpoint.Received(1).PublishAsync(Arg.Any<ResponseMsg>(), Arg.Any<CancellationToken>());
        await sendEndpointProvider.DidNotReceive().GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>());
    }

    // ── Priority: AMQP ReplyTo wins over envelope responseAddress ───────────────

    [Fact]
    public async Task RespondAsync_WithBothReplyToHeaderAndMtRouting_PrefersReplyToHeader()
    {
        // Arrange — when AMQP ReplyTo is present, it takes priority over MT envelope routing
        var headers = new Dictionary<string, string>
        {
            ["ReplyTo"] = "amq.gen-amqp-reply-queue",
        };

        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/mt-envelope-reply",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: Guid.NewGuid(),
            CorrelationId: null,
            ExpirationTime: null);

        Uri? capturedUri = null;
        ISendEndpoint sendEndpoint = Substitute.For<ISendEndpoint>();
        sendEndpoint.SendAsync(Arg.Any<ResponseMsg>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();
        sendEndpointProvider.GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
                            .Returns(callInfo =>
                            {
                                capturedUri = callInfo.ArgAt<Uri>(0);
                                return Task.FromResult(sendEndpoint);
                            });

        var ctx = CreateContextWithMtRouting(inboundRouting, new CapturingResponseWriter(),
            headers: headers, sendEndpointProvider: sendEndpointProvider);

        // Act
        await ctx.RespondAsync(new ResponseMsg("prefer-reply-to"), CancellationToken.None);

        // Assert: routes via the existing ReplyTo path (queue: scheme), not MT envelope path
        capturedUri.Should().NotBeNull();
        capturedUri!.Scheme.Should().Be("queue");
        capturedUri.AbsolutePath.Should().Contain("amqp-reply-queue");
    }

    // ── DEFECT 1 regression guard: payload handed to SendRawAsync must be owned ───

    /// <summary>
    /// Regression guard for the use-after-return bug: the payload captured by SendRawAsync
    /// must be an independent, owned array — not a view into a rented ArrayPool buffer that
    /// is returned to the pool before the channel consumer reads it.
    ///
    /// We verify that the captured bytes still form a valid JSON MT envelope after the
    /// RespondAsync call returns (at which point any rented buffer would have been returned).
    /// Additionally we confirm the payload is non-empty and starts with '{' — the minimum
    /// structural requirement for a JSON envelope.
    /// </summary>
    [Fact]
    public async Task RespondAsync_WithMtRouting_PayloadIsOwnedAndIndependentOfPooledBuffer()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var inboundRouting = new RequestEnvelopeContext(
            ResponseAddress: "rabbitmq://localhost/reply-queue",
            DestinationAddress: null,
            FaultAddress: null,
            RequestId: requestId,
            CorrelationId: null,
            ExpirationTime: null);

        var writer = new CapturingResponseWriter();

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

        var ctx = CreateContextWithMtRouting(inboundRouting, writer,
            sendEndpointProvider: sendEndpointProvider);

        // Act
        await ctx.RespondAsync(new ResponseMsg("ownership-check"), CancellationToken.None);

        // Force GC pressure so any returned pooled arrays are likely re-rented by other threads.
        // This is a best-effort probe; correctness relies on the payload being a standalone array.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        // Assert: the captured payload must be non-empty, start with '{' (JSON object), and
        // contain the requestId — proving the bytes are intact after the rented buffer is returned.
        capturedPayload.IsEmpty.Should().BeFalse("SendRawAsync must receive a non-empty payload");
        capturedPayload.Span[0].Should().Be((byte)'{', "payload must be a JSON object");

        string json = System.Text.Encoding.UTF8.GetString(capturedPayload.Span);
        json.Should().Contain(requestId.ToString(), "requestId must survive buffer return");
    }

    // ── Existing fallback tests remain unaffected (regression guard) ─────────────

    [Fact]
    public async Task RespondAsync_WithNoRoutingAtAll_FallsBackToPublishAsync()
    {
        // Arrange — no ReplyTo, no MT routing on context
        var headers = new Dictionary<string, string>();
        IPublishEndpoint publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.PublishAsync(Arg.Any<ResponseMsg>(), Arg.Any<CancellationToken>())
                       .Returns(Task.CompletedTask);

        ISendEndpointProvider sendEndpointProvider = Substitute.For<ISendEndpointProvider>();

        // Create a plain context without MT routing
        var ctx = new RespondTestableConsumeContext(
            Guid.NewGuid(), null, null, null, null, null,
            headers, null, default,
            publishEndpoint, sendEndpointProvider);

        // Act
        await ctx.RespondAsync(new ResponseMsg("broadcast"), CancellationToken.None);

        // Assert
        await publishEndpoint.Received(1).PublishAsync(Arg.Any<ResponseMsg>(), Arg.Any<CancellationToken>());
        await sendEndpointProvider.DidNotReceive().GetSendEndpoint(Arg.Any<Uri>(), Arg.Any<CancellationToken>());
    }
}
