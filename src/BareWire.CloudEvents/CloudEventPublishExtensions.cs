using BareWire.Abstractions;

namespace BareWire.CloudEvents;

/// <summary>
/// Provides extension methods on <see cref="IPublishEndpoint"/> for publishing messages
/// with CloudEvents 1.0 context attributes encoded as binary-mode <c>ce-*</c> transport headers.
/// </summary>
/// <remarks>
/// <para>
/// Binary mode (ADR-007): the message payload is carried raw without an envelope (ADR-001).
/// CE context attributes are mapped to transport headers via
/// <c>CloudEventBinaryHeaderMapper.ToHeaders</c> (13.4/13.5) and passed to the existing
/// <see cref="IPublishEndpoint.PublishAsync{T}(T, System.Collections.Generic.IReadOnlyDictionary{string,string}?, System.Threading.CancellationToken)"/>
/// overload. No <c>byte[]</c> is allocated per message in the hot path (ADR-003).
/// </para>
/// <para>
/// Because <see cref="IBus"/> and <see cref="ConsumeContext"/> both implement
/// <see cref="IPublishEndpoint"/>, this extension method covers all publish call sites with
/// a single signature.
/// </para>
/// </remarks>
public static class CloudEventPublishExtensions
{
    /// <summary>
    /// Publishes <paramref name="message"/> with CloudEvents 1.0 context attributes encoded
    /// as <c>ce-*</c> binary-mode transport headers.
    /// </summary>
    /// <typeparam name="T">The message type. Must be a reference type.</typeparam>
    /// <param name="endpoint">The publish endpoint to send the message through. Must not be <see langword="null"/>.</param>
    /// <param name="message">The message payload to publish. Must not be <see langword="null"/>.</param>
    /// <param name="attributes">
    /// The CloudEvents 1.0 context attributes to encode as <c>ce-*</c> headers.
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
    /// <exception cref="BareWire.Abstractions.Exceptions.BareWireSerializationException">
    /// Thrown fail-fast (before publish) when any mandatory CE attribute is missing/empty or
    /// when <c>specversion</c> is not <c>"1.0"</c>. The <see cref="IPublishEndpoint"/> is
    /// never called when validation fails.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Validation (13.3) is performed <em>before</em> building the header dictionary and before
    /// calling <see cref="IPublishEndpoint.PublishAsync{T}(T, System.Collections.Generic.IReadOnlyDictionary{string,string}?, System.Threading.CancellationToken)"/>,
    /// ensuring that only well-formed CloudEvents are emitted to the transport.
    /// </para>
    /// <para>
    /// The implementation allocates one <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
    /// for the header set (pre-sized to 8 slots); no <c>byte[]</c> is allocated per call (ADR-003).
    /// The cast to <see cref="System.Collections.Generic.IReadOnlyDictionary{TKey,TValue}"/> is
    /// a safe upcast — no boxing or copy.
    /// </para>
    /// </remarks>
    public static Task PublishCloudEventAsync<T>(
        this IPublishEndpoint endpoint,
        T message,
        ICloudEventAttributes attributes,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(attributes);

        // Fail-fast validation (13.3) — reject invalid CE attributes BEFORE touching the transport.
        CloudEventAttributeValidator.ValidateMandatory(attributes, CloudEventsBinaryContentType.Value);

        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(attributes);
        return endpoint.PublishAsync(message, (IReadOnlyDictionary<string, string>)headers, cancellationToken);
    }
}
