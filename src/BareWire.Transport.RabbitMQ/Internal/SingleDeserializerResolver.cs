using BareWire.Abstractions.Serialization;

namespace BareWire.Transport.RabbitMQ.Internal;

/// <summary>
/// Wraps a single <see cref="IMessageDeserializer"/> as an <see cref="IDeserializerResolver"/>.
/// Used as a fallback by <see cref="RabbitMqRequestClientFactory"/> when no content-type-routed
/// <see cref="IDeserializerResolver"/> is registered, preserving the pre-issue-#13 behaviour for
/// configurations that only register a default deserializer.
/// </summary>
/// <remarks>
/// This is a transport-local mirror of the core <c>BareWire.Serialization.SingleDeserializerResolver</c>.
/// It exists here because <c>BareWire.Transport.RabbitMQ</c> depends on <c>BareWire.Abstractions</c> only
/// and must not reference the core <c>BareWire</c> package.
/// </remarks>
internal sealed class SingleDeserializerResolver(IMessageDeserializer deserializer) : IDeserializerResolver
{
    private readonly IMessageDeserializer _deserializer =
        deserializer ?? throw new ArgumentNullException(nameof(deserializer));

    public IMessageDeserializer Resolve(string? contentType) => _deserializer;
}
