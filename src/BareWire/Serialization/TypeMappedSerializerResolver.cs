using BareWire.Abstractions.Serialization;

namespace BareWire.Serialization;

/// <summary>
/// Implementation of <see cref="ISerializerResolver"/> that performs a per-type dictionary lookup
/// and falls back to a default serializer when no mapping is registered for the requested type.
/// </summary>
/// <remarks>
/// Thread-safe after construction — the mapping dictionary is read-only once the resolver is built.
/// All <see cref="IMessageSerializer"/> values are pre-resolved DI singletons, so
/// <see cref="Resolve{T}"/> performs no allocations in the hot path.
/// </remarks>
internal sealed class TypeMappedSerializerResolver : ISerializerResolver
{
    private readonly IMessageSerializer _default;
    private readonly IReadOnlyDictionary<Type, IMessageSerializer> _mappings;

    internal TypeMappedSerializerResolver(
        IMessageSerializer defaultSerializer,
        IReadOnlyDictionary<Type, IMessageSerializer> mappings)
    {
        _default = defaultSerializer ?? throw new ArgumentNullException(nameof(defaultSerializer));
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
    }

    /// <inheritdoc />
    public IMessageSerializer Resolve<T>() where T : class
        => _mappings.TryGetValue(typeof(T), out IMessageSerializer? serializer) ? serializer : _default;
}
