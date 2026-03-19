using System.Buffers;

namespace BareWire.Abstractions;

/// <summary>
/// Extends <see cref="ConsumeContext"/> with a strongly-typed message payload.
/// Instances are created exclusively by the pipeline infrastructure and passed to
/// <see cref="IConsumer{T}.ConsumeAsync"/> implementations.
/// </summary>
/// <typeparam name="T">
/// The message type. Must be a reference type (typically a <c>record</c>).
/// </typeparam>
public sealed class ConsumeContext<T> : ConsumeContext where T : class
{
    /// <summary>
    /// Gets the deserialized message payload.
    /// </summary>
    public T Message { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="ConsumeContext{T}"/>.
    /// Only callable by the pipeline infrastructure.
    /// </summary>
    internal ConsumeContext(
        T message,
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
        : base(messageId, correlationId, conversationId, sourceAddress, destinationAddress,
               sentTime, headers, contentType, rawBody, publishEndpoint, sendEndpointProvider,
               cancellationToken)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }
}
