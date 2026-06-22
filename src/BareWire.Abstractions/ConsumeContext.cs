using System.Buffers;

using BareWire.Abstractions.Serialization;

namespace BareWire.Abstractions;

/// <summary>
/// Provides the base context available to all consumer handlers, carrying message metadata and the
/// capability to publish new messages or send to specific endpoints from within a consumer.
/// Instances are created by the pipeline infrastructure with <see langword="internal"/> constructors;
/// they must not be instantiated directly by application code.
/// </summary>
/// <remarks>
/// This abstract class is the base for <see cref="ConsumeContext{T}"/> (typed consumers) and
/// <see cref="RawConsumeContext"/> (raw consumers). The full implementation — including
/// <c>PublishAsync</c>, <c>RespondAsync</c>, and <c>GetSendEndpoint</c> — is provided in
/// the concrete subclasses created by the pipeline.
/// </remarks>
public abstract class ConsumeContext : IPublishEndpoint, ISendEndpointProvider
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ISendEndpointProvider _sendEndpointProvider;

    /// <summary>
    /// Gets the unique identifier of the message being consumed.
    /// </summary>
    public Guid MessageId { get; }

    /// <summary>
    /// Gets the optional correlation identifier used to correlate this message with a related
    /// conversation or saga instance. <see langword="null"/> when not set by the publisher.
    /// </summary>
    public Guid? CorrelationId { get; }

    /// <summary>
    /// Gets the optional conversation identifier that groups a chain of related messages.
    /// <see langword="null"/> when not set by the publisher.
    /// </summary>
    public Guid? ConversationId { get; }

    /// <summary>
    /// Gets the transport address of the endpoint that originated this message.
    /// </summary>
    public Uri? SourceAddress { get; }

    /// <summary>
    /// Gets the transport address of the endpoint where this message was delivered.
    /// </summary>
    public Uri? DestinationAddress { get; }

    /// <summary>
    /// Gets the UTC time at which the message was sent by the publisher.
    /// <see langword="null"/> when the publisher did not include a sent-time header.
    /// </summary>
    public DateTimeOffset? SentTime { get; }

    /// <summary>
    /// Gets the transport-level and application-level headers attached to the message.
    /// Never null — an empty dictionary is returned when no headers are present.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// Gets the MIME content type of the raw message body (e.g. <c>"application/json"</c>).
    /// <see langword="null"/> when no content-type header was present on the inbound message.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Gets the raw zero-copy body of the message as received from the transport.
    /// The sequence is valid only for the duration of the consume callback; it must not be retained.
    /// </summary>
    public ReadOnlySequence<byte> RawBody { get; }

    /// <summary>
    /// Gets the cancellation token that signals that message processing should be aborted.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="ConsumeContext"/>.
    /// Only callable by the pipeline infrastructure via <see langword="internal"/> derived constructors.
    /// </summary>
    internal ConsumeContext(
        Guid messageId,
        Guid? correlationId,
        Guid? conversationId,
        Uri? sourceAddress,
        Uri? destinationAddress,
        DateTimeOffset? sentTime,
        IReadOnlyDictionary<string, string> headers,
        string? contentType,
        ReadOnlySequence<byte> rawBody,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        CancellationToken cancellationToken = default)
    {
        MessageId = messageId;
        CorrelationId = correlationId;
        ConversationId = conversationId;
        SourceAddress = sourceAddress;
        DestinationAddress = destinationAddress;
        SentTime = sentTime;
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        ContentType = contentType;
        RawBody = rawBody;
        _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
        _sendEndpointProvider = sendEndpointProvider ?? throw new ArgumentNullException(nameof(sendEndpointProvider));
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets or sets the inbound request routing metadata extracted from an MT envelope.
    /// Set by <c>ConsumerInvokerFactory</c> in BareWire core when the deserializer implements
    /// <see cref="IRequestEnvelopeRouteReader"/> and the content-type is the MT JSON envelope.
    /// <see langword="null"/> for non-MT messages.
    /// </summary>
    internal RequestEnvelopeContext? InboundRequestContext { get; set; }

    /// <summary>
    /// Gets or sets the response envelope writer used to serialize the reply envelope when
    /// handling an MT request. Set by <c>ConsumerInvokerFactory</c> alongside
    /// <see cref="InboundRequestContext"/>. <see langword="null"/> for non-MT messages.
    /// </summary>
    internal IResponseEnvelopeWriter? ResponseEnvelopeWriter { get; set; }

    /// <summary>
    /// Publishes a typed message to all consumers subscribed to <typeparamref name="T"/>
    /// from within this consumer handler, preserving the conversation context.
    /// </summary>
    /// <inheritdoc cref="IPublishEndpoint.PublishAsync{T}(T, CancellationToken)"/>
    public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : class
        => _publishEndpoint.PublishAsync(message, cancellationToken);

    /// <summary>
    /// Publishes a typed message with custom transport headers from within this consumer handler.
    /// </summary>
    /// <inheritdoc cref="IPublishEndpoint.PublishAsync{T}(T, IReadOnlyDictionary{string, string}?, CancellationToken)"/>
    public Task PublishAsync<T>(T message, IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken = default)
        where T : class
        => _publishEndpoint.PublishAsync(message, headers, cancellationToken);

    /// <summary>
    /// Publishes a raw payload from within this consumer handler.
    /// </summary>
    /// <inheritdoc cref="IPublishEndpoint.PublishRawAsync"/>
    public Task PublishRawAsync(ReadOnlyMemory<byte> payload, string contentType,
        CancellationToken cancellationToken = default)
        => _publishEndpoint.PublishRawAsync(payload, contentType, cancellationToken);

    /// <summary>
    /// Resolves a send endpoint by address, allowing point-to-point messaging from within a consumer.
    /// </summary>
    /// <inheritdoc cref="ISendEndpointProvider.GetSendEndpoint"/>
    public Task<ISendEndpoint> GetSendEndpoint(Uri address, CancellationToken cancellationToken = default)
        => _sendEndpointProvider.GetSendEndpoint(address, cancellationToken);

    /// <summary>
    /// Sends a response message back to the originator of the current message.
    /// When a <c>ReplyTo</c> header is present, the response is delivered directly to that address.
    /// Falls back to <see cref="PublishAsync{T}(T, CancellationToken)"/> for backwards compatibility when no reply address is set.
    /// </summary>
    /// <typeparam name="T">The response message type. Must be a reference type.</typeparam>
    /// <param name="response">The response message to send.</param>
    /// <param name="cancellationToken">A token to cancel the send operation.</param>
    /// <returns>A <see cref="Task"/> that completes when the response has been accepted by the transport.</returns>
    public virtual async Task RespondAsync<T>(T response, CancellationToken cancellationToken = default)
        where T : class
    {
        // Priority 1: transport AMQP ReplyTo header (BareWire→BareWire, or MT with AMQP ReplyTo set).
        // This path is preserved exactly as before.
        if (Headers.TryGetValue("ReplyTo", out string? replyTo) && !string.IsNullOrEmpty(replyTo))
        {
            // Build a queue-scheme URI that signals direct-to-queue delivery via the default exchange.
            // Correlation-id is encoded as a query parameter so the send endpoint can forward it
            // to the transport (required by the request client to match responses).
            Headers.TryGetValue("correlation-id", out string? correlationId);

            string uriString = correlationId is not null
                ? $"queue://localhost/{Uri.EscapeDataString(replyTo)}?correlation-id={Uri.EscapeDataString(correlationId)}"
                : $"queue://localhost/{Uri.EscapeDataString(replyTo)}";

            ISendEndpoint endpoint = await GetSendEndpoint(new Uri(uriString), cancellationToken)
                .ConfigureAwait(false);
            await endpoint.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Priority 2: MassTransit envelope routing — inbound request carried responseAddress + requestId
        // inside the MT JSON envelope body. The deserializer extracted these and placed them in
        // InboundRequestContext. Route the reply directly to the MT reply queue and echo requestId
        // so the MT IRequestClient<T> can correlate the response. (SEC-1: sanitize responseAddress.)
        if (InboundRequestContext.HasValue && ResponseEnvelopeWriter is not null)
        {
            RequestEnvelopeContext routing = InboundRequestContext.Value;
            Uri? replyUri = TryBuildMtReplyUri(routing.ResponseAddress);

            if (replyUri is not null)
            {
                // Serialize the MT response envelope (echo requestId) into a growable pooled buffer.
                // DEFECT 2 fix: GrowablePooledBufferWriter grows on overflow — no fixed 4 KiB cap.
                // DEFECT 1 fix: the payload passed to SendRawAsync is an owned copy (ToArray()) so
                // the channel consumer reads stable bytes after the rented buffer is returned.
                // ToArray() on the cold request/response path is the deliberate exception to ADR-003.
                ReadOnlyMemory<byte> payload;
                using (var bufferWriter = new GrowablePooledBufferWriter())
                {
                    ResponseEnvelopeWriter.WriteResponse(response, routing.RequestId, bufferWriter);
                    // Own the bytes before the using block returns the rented buffer to the pool.
                    payload = bufferWriter.WrittenSpan.ToArray();
                }

                ISendEndpoint endpoint = await GetSendEndpoint(replyUri, cancellationToken)
                    .ConfigureAwait(false);
                await endpoint.SendRawAsync(payload, "application/vnd.masstransit+json", cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            // responseAddress is absent or has an untrusted scheme — fall through to PublishAsync.
            System.Diagnostics.Trace.TraceWarning(
                $"[BareWire] RespondAsync: MT routing present but responseAddress is absent or has " +
                $"an unsupported scheme on message {MessageId}. Falling back to PublishAsync.");
        }

        // Priority 3 (fallback): publish to all subscribers when no reply address is available.
        // In a request-response scenario this almost certainly indicates a misconfigured consumer.
        System.Diagnostics.Trace.TraceWarning(
            $"[BareWire] RespondAsync called without a ReplyTo header on message {MessageId}. " +
            "Falling back to PublishAsync — response will be broadcast to all subscribers.");
        await PublishAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a <c>queue://localhost/&lt;queueName&gt;</c> URI from a MassTransit
    /// <c>responseAddress</c> field, applying SEC-1 sanitization:
    /// strips host/authority/UserInfo and takes only the last path segment as the queue name.
    /// Returns <see langword="null"/> when the scheme is not <c>rabbitmq</c> or the address
    /// is null/empty.
    /// </summary>
    private static Uri? TryBuildMtReplyUri(string? responseAddress)
    {
        if (string.IsNullOrEmpty(responseAddress))
            return null;

        if (!Uri.TryCreate(responseAddress, UriKind.Absolute, out Uri? parsed))
            return null;

        // SEC-1: only trust rabbitmq:// scheme; reject amqp://, http://, etc.
        if (!parsed.Scheme.Equals("rabbitmq", StringComparison.OrdinalIgnoreCase))
            return null;

        // SEC-1: take only the last path segment as the queue name; strip host/vhost/UserInfo.
        string[] segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string queueName = segments.Length > 0 ? segments[^1] : string.Empty;

        if (string.IsNullOrEmpty(queueName))
            return null;

        return new Uri($"queue://localhost/{Uri.EscapeDataString(queueName)}");
    }

    /// <summary>
    /// A growable <see cref="IBufferWriter{T}"/> backed by rented <see cref="ArrayPool{T}"/> arrays.
    /// Used only within <see cref="RespondAsync{T}"/> to avoid taking a dependency on
    /// <c>PooledBufferWriter</c> from BareWire core (which would violate the zero-dependency
    /// constraint on <c>BareWire.Abstractions</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the current buffer is exhausted, <see cref="Grow"/> rents a larger array
    /// (at least 2× or <c>WrittenCount + sizeHint</c>), copies existing bytes via
    /// <see cref="Buffer.BlockCopy"/>, and returns the old array to the pool.
    /// All rented arrays are returned in <see cref="Dispose"/> — even when serialization throws.
    /// </para>
    /// <para>
    /// <see cref="GetSpan"/> and <see cref="GetMemory"/> honor <c>sizeHint</c> and grow eagerly
    /// so that <see cref="System.Text.Json.Utf8JsonWriter"/> never encounters a buffer it cannot
    /// write into (fixing the fixed-4096-byte overflow, DEFECT 2).
    /// </para>
    /// </remarks>
    private sealed class GrowablePooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer;
        private int _position;
        private bool _disposed;

        internal GrowablePooledBufferWriter(int initialCapacity = 4096)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        }

        internal int WrittenCount => _position;

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

        public void Advance(int count) => _position += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_position);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_position);
        }

        private void EnsureCapacity(int sizeHint)
        {
            int needed = sizeHint <= 0 ? 1 : sizeHint;
            int available = _buffer.Length - _position;

            if (available >= needed)
                return;

            Grow(needed);
        }

        private void Grow(int sizeHint)
        {
            int minimumSize = Math.Max(_buffer.Length * 2, _position + sizeHint);
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(minimumSize);

            Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _position);

            // Swap before returning the old buffer so the writer always tracks exactly one live
            // rent (Dispose can reclaim it) — no window where a returned buffer is still referenced.
            byte[] oldBuffer = _buffer;
            _buffer = newBuffer;
            ArrayPool<byte>.Shared.Return(oldBuffer);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null!;
        }
    }
}
