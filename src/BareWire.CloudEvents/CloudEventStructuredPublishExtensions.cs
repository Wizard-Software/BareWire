using System.Buffers;

using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;

namespace BareWire.CloudEvents;

/// <summary>
/// Provides extension methods on <see cref="IPublishEndpoint"/> for publishing messages
/// in CloudEvents 1.0 structured content mode (<c>application/cloudevents+json</c> envelope).
/// </summary>
/// <remarks>
/// <para>
/// Structured content mode encodes both the CloudEvents context attributes and the event data
/// in a single JSON document. Use <see cref="PublishCloudEventStructuredAsync{T}"/> to build
/// and publish such an envelope via the BareWire transport.
/// </para>
/// <para>
/// This class is <see langword="public"/> and <see langword="static"/> because it contains
/// extension methods — an explicit exception to the <c>internal</c> visibility rule that
/// applies to all other implementation classes in <c>BareWire.CloudEvents</c>.
/// </para>
/// </remarks>
public static class CloudEventStructuredPublishExtensions
{
    /// <summary>
    /// Publishes <paramref name="message"/> as a CloudEvents 1.0 structured-mode envelope
    /// (<c>application/cloudevents+json</c>), embedding <paramref name="attributes"/> as
    /// top-level CE fields and the serialized <paramref name="message"/> as the <c>data</c> property.
    /// </summary>
    /// <typeparam name="T">The message type. Must be a reference type.</typeparam>
    /// <param name="endpoint">The publish endpoint to send the message through. Must not be <see langword="null"/>.</param>
    /// <param name="message">The message payload to embed in the <c>data</c> field. Must not be <see langword="null"/>.</param>
    /// <param name="attributes">
    /// The CloudEvents 1.0 context attributes to embed as envelope fields.
    /// Must not be <see langword="null"/>. All four mandatory CE attributes must be valid
    /// (non-empty, with <c>specversion == "1.0"</c>) — validated fail-fast before publish.
    /// </param>
    /// <param name="cancellationToken">
    /// A <see cref="CancellationToken"/> to observe while waiting for the operation to complete.
    /// </param>
    /// <returns>A <see cref="Task"/> representing the asynchronous publish operation.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="endpoint"/>, <paramref name="message"/>, or
    /// <paramref name="attributes"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="BareWireSerializationException">
    /// Thrown fail-fast (before publish) when any mandatory CE attribute is missing/empty or
    /// when <c>specversion</c> is not <c>"1.0"</c>. The <see cref="IPublishEndpoint"/> is
    /// never called when validation fails.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Validation (13.3) is performed <em>before</em> any serialization or transport interaction,
    /// ensuring that only well-formed CloudEvents envelopes are emitted. A <see cref="BareWireSerializationException"/>
    /// is thrown immediately if mandatory attributes fail validation.
    /// </para>
    /// <para>
    /// The envelope is written into an <see cref="ArrayBufferWriter{T}"/> whose capacity grows
    /// to fit exactly one envelope. This is distinct from <c>CloudEventsEnvelopeLimits.MaxEnvelopeSizeBytes</c>,
    /// which is a consume-side guard applied during deserialization. The emitted envelope size
    /// is the caller's responsibility and may exceed a consumer's configured <c>MaxEnvelopeSizeBytes</c>
    /// (publish/consume size-limit asymmetry; the publisher is a trusted-input side).
    /// </para>
    /// <para>
    /// <see cref="ArrayBufferWriter{T}"/> is used instead of a pooled writer because
    /// <see cref="IPublishEndpoint.PublishRawAsync"/> stores the payload as a <see cref="ReadOnlyMemory{T}"/>
    /// view in the outgoing channel and consumes it asynchronously after this method returns.
    /// A pooled buffer returned to the pool on method exit would create a use-after-free condition.
    /// The buffer is GC-managed and safe for the lifetime of the async message delivery.
    /// </para>
    /// </remarks>
    public static Task PublishCloudEventStructuredAsync<T>(
        this IPublishEndpoint endpoint,
        T message,
        ICloudEventAttributes attributes,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(attributes);

        // Fail-fast validation (13.3) — reject invalid CE attributes BEFORE any allocation or transport call.
        CloudEventAttributeValidator.ValidateMandatory(attributes, CloudEventsEnvelopeContentType.Value);

        // Build the CloudEvents structured envelope. CloudEventsEnvelopeSerializer is internal
        // and called directly in-package; it must NOT be made public.
        var serializer = new CloudEventsEnvelopeSerializer(attributes);
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(message, buffer);

        // WrittenMemory is a GC-owned view into the ArrayBufferWriter's internal array.
        // It is safe to pass through PublishRawAsync's async outgoing channel — no defensive .ToArray() needed.
        return endpoint.PublishRawAsync(buffer.WrittenMemory, CloudEventsEnvelopeContentType.Value, cancellationToken);
    }
}
