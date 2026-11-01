using BareWire.Abstractions.Serialization;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Wraps a single <see cref="IMessageDeserializer"/> as an <see cref="IDeserializerResolver"/>. Used as a
/// fallback by <see cref="InMemoryRequestClientFactory"/> when no content-type-routed
/// <see cref="IDeserializerResolver"/> is registered, so a container that only registers a default
/// deserializer (for example the test harness) still builds a bus.
/// </summary>
/// <remarks>
/// A transport-local mirror of the core <c>BareWire.Serialization.SingleDeserializerResolver</c>:
/// <c>BareWire.Transport.InMemory</c> depends on <c>BareWire.Abstractions</c> only and must not reference
/// the core <c>BareWire</c> package.
/// </remarks>
internal sealed class SingleDeserializerResolver(IMessageDeserializer deserializer) : IDeserializerResolver
{
    private readonly IMessageDeserializer _deserializer =
        deserializer ?? throw new ArgumentNullException(nameof(deserializer));

    public IMessageDeserializer Resolve(string? contentType) => _deserializer;
}
