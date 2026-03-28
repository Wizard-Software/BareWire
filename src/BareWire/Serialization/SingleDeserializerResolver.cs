using BareWire.Abstractions.Serialization;

namespace BareWire.Serialization;

/// <summary>
/// Wraps a single <see cref="IMessageDeserializer"/> as an <see cref="IDeserializerResolver"/>.
/// Used as a fallback when no multi-format resolver is registered and as a per-endpoint override wrapper.
/// </summary>
internal sealed class SingleDeserializerResolver(IMessageDeserializer deserializer) : IDeserializerResolver
{
    public IMessageDeserializer Resolve(string? contentType) => deserializer;
}
