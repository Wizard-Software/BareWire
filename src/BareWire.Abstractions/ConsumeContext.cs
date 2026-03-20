using System.Buffers;

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
    /// Publishes a typed message to all consumers subscribed to <typeparamref name="T"/>
    /// from within this consumer handler, preserving the conversation context.
    /// </summary>
    /// <inheritdoc cref="IPublishEndpoint.PublishAsync{T}"/>
    public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : class
        => _publishEndpoint.PublishAsync(message, cancellationToken);

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
    /// Falls back to <see cref="PublishAsync{T}"/> for backwards compatibility when no reply address is set.
    /// </summary>
    /// <typeparam name="T">The response message type. Must be a reference type.</typeparam>
    /// <param name="response">The response message to send.</param>
    /// <param name="cancellationToken">A token to cancel the send operation.</param>
    /// <returns>A <see cref="Task"/> that completes when the response has been accepted by the transport.</returns>
    public virtual async Task RespondAsync<T>(T response, CancellationToken cancellationToken = default)
        where T : class
    {
        // If a ReplyTo header exists, send the response directly to that address (request-response pattern).
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

        // Fallback: publish to all subscribers when no ReplyTo header is present.
        // WARNING: this means the response is broadcast to all subscribers rather than returned to
        // the original caller. In a request-response scenario this almost certainly indicates a
        // misconfigured consumer or a message that was not sent via IRequestClient<T>.
        // No logger is available here (BareWire.Abstractions has no infrastructure dependencies),
        // so a Trace warning is emitted to make the condition visible during debugging.
        System.Diagnostics.Trace.TraceWarning(
            $"[BareWire] RespondAsync called without a ReplyTo header on message {MessageId}. " +
            "Falling back to PublishAsync — response will be broadcast to all subscribers.");
        await PublishAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
