using System.Buffers;
using BareWire.Abstractions.Serialization;

namespace BareWire.Abstractions;

/// <summary>
/// Extends <see cref="ConsumeContext"/> for raw (undeserialized) message consumption.
/// Instances are created exclusively by the pipeline infrastructure and passed to
/// <see cref="IRawConsumer.ConsumeAsync"/> implementations.
/// Provides on-demand deserialization via <see cref="TryDeserialize{T}"/>.
/// </summary>
public sealed class RawConsumeContext : ConsumeContext
{
    private readonly IMessageDeserializer _deserializer;

    /// <summary>
    /// Initializes a new instance of <see cref="RawConsumeContext"/>.
    /// Only callable by the pipeline infrastructure.
    /// </summary>
    internal RawConsumeContext(
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
        IMessageDeserializer deserializer,
        CancellationToken cancellationToken = default)
        : base(messageId, correlationId, conversationId, sourceAddress, destinationAddress,
               sentTime, headers, contentType, rawBody, publishEndpoint, sendEndpointProvider,
               cancellationToken)
    {
        _deserializer = deserializer ?? throw new ArgumentNullException(nameof(deserializer));
    }

    /// <summary>
    /// Attempts to deserialize the raw body into an instance of <typeparamref name="T"/>.
    /// Returns <see langword="null"/> if deserialization fails or the body is empty.
    /// This operation is lazy — deserialization only occurs when this method is called.
    /// </summary>
    /// <typeparam name="T">The target message type. Must be a reference type.</typeparam>
    /// <returns>The deserialized instance, or <see langword="null"/> on failure.</returns>
    public T? TryDeserialize<T>() where T : class
    {
        if (RawBody.IsEmpty)
        {
            return null;
        }

        try
        {
            return _deserializer.Deserialize<T>(RawBody);
        }
        catch
        {
            return null;
        }
    }

}
