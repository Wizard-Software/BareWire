using BareWire.Abstractions.Serialization;

namespace BareWire.CloudEvents;

/// <summary>
/// A package-local <see cref="IDeserializerResolver"/> decorator that routes
/// <c>application/cloudevents+json</c> content to <see cref="CloudEventsEnvelopeDeserializer"/>
/// and delegates all other content types to the wrapped inner resolver (raw-JSON fallback — ADR-001).
/// </summary>
/// <remarks>
/// <para>
/// This router is separate from the one in <c>BareWire.Serialization.Json</c>
/// (which is <see langword="internal"/> there and unreachable from this package).
/// <c>BareWire.CloudEvents</c> depends solely on <c>BareWire.Abstractions</c> (NetArchTest 13.14),
/// so it carries its own minimal router contracted by the public <see cref="IDeserializerResolver"/> interface.
/// </para>
/// <para>
/// Content-type comparison uses <see cref="StringComparison.OrdinalIgnoreCase"/> exact-match —
/// non-CE content types (including those with parameters such as <c>charset=utf-8</c>)
/// are delegated to the inner resolver unchanged (fail-closed, ADR-001).
/// </para>
/// </remarks>
internal sealed class ContentTypeDeserializerRouter : IDeserializerResolver
{
    private readonly IDeserializerResolver _inner;
    private readonly IMessageDeserializer _cloudEventsDeserializer;

    /// <summary>
    /// Initializes a new instance of <see cref="ContentTypeDeserializerRouter"/>.
    /// </summary>
    /// <param name="inner">The fallback resolver for all non-CE content types. Must not be <see langword="null"/>.</param>
    /// <param name="cloudEventsDeserializer">
    /// The deserializer for <c>application/cloudevents+json</c> payloads. Must not be <see langword="null"/>.
    /// </param>
    internal ContentTypeDeserializerRouter(IDeserializerResolver inner, IMessageDeserializer cloudEventsDeserializer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cloudEventsDeserializer);
        _inner = inner;
        _cloudEventsDeserializer = cloudEventsDeserializer;
    }

    /// <inheritdoc/>
    public IMessageDeserializer Resolve(string? contentType)
        => string.Equals(contentType, CloudEventsEnvelopeContentType.Value, StringComparison.OrdinalIgnoreCase)
            ? _cloudEventsDeserializer
            : _inner.Resolve(contentType);
}
