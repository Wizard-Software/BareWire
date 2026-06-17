using BareWire.Abstractions.Serialization;

namespace BareWire.Serialization.MsgPack;

/// <summary>
/// A package-local <see cref="IDeserializerResolver"/> decorator that routes
/// <c>application/x-msgpack</c> content to <see cref="MessagePackDeserializer"/>
/// and delegates all other content types to the wrapped inner resolver (raw-JSON fallback — ADR-001).
/// </summary>
/// <remarks>
/// <para>
/// This router is separate from the one in <c>BareWire.Serialization.Json</c>
/// (which is <see langword="internal"/> there and unreachable from this package).
/// <c>BareWire.Serialization.MsgPack</c> depends solely on <c>BareWire.Abstractions</c>,
/// so it carries its own minimal router contracted by the public <see cref="IDeserializerResolver"/>
/// interface — mirroring the decorator pattern established by <c>BareWire.CloudEvents</c>.
/// </para>
/// <para>
/// Content-type comparison uses <see cref="StringComparison.OrdinalIgnoreCase"/> exact-match —
/// non-MsgPack content types (including those with parameters such as <c>charset=utf-8</c>)
/// are delegated to the inner resolver unchanged (fail-closed, ADR-001).
/// Parameterised variants like <c>application/x-msgpack; charset=utf-8</c> are therefore
/// NOT matched and fall through to the inner chain, preserving the conservative fail-closed
/// behaviour identical to <c>BareWire.CloudEvents.ContentTypeDeserializerRouter</c>.
/// </para>
/// </remarks>
internal sealed class MessagePackDeserializerRouter : IDeserializerResolver
{
    private readonly IDeserializerResolver _inner;
    private readonly IMessageDeserializer _messagePackDeserializer;

    /// <summary>
    /// Initializes a new instance of <see cref="MessagePackDeserializerRouter"/>.
    /// </summary>
    /// <param name="inner">The fallback resolver for all non-MsgPack content types. Must not be <see langword="null"/>.</param>
    /// <param name="messagePackDeserializer">
    /// The deserializer for <c>application/x-msgpack</c> payloads. Must not be <see langword="null"/>.
    /// </param>
    internal MessagePackDeserializerRouter(IDeserializerResolver inner, IMessageDeserializer messagePackDeserializer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(messagePackDeserializer);
        _inner = inner;
        _messagePackDeserializer = messagePackDeserializer;
    }

    /// <inheritdoc/>
    public IMessageDeserializer Resolve(string? contentType)
        => string.Equals(contentType, _messagePackDeserializer.ContentType, StringComparison.OrdinalIgnoreCase)
            ? _messagePackDeserializer
            : _inner.Resolve(contentType);
}
