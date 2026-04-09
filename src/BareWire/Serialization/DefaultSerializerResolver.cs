using BareWire.Abstractions.Serialization;

namespace BareWire.Serialization;

/// <summary>
/// Default implementation of <see cref="ISerializerResolver"/> that returns the same injected
/// <see cref="IMessageSerializer"/> for every message type. Used when no per-type mappings are
/// configured via <c>IBusConfigurator.MapSerializer&lt;TMessage, TSerializer&gt;()</c>.
/// </summary>
internal sealed class DefaultSerializerResolver : ISerializerResolver
{
    private readonly IMessageSerializer _serializer;

    internal DefaultSerializerResolver(IMessageSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    /// <inheritdoc />
    public IMessageSerializer Resolve<T>() where T : class => _serializer;
}
