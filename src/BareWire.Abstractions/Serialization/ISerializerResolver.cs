namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Resolves the <see cref="IMessageSerializer"/> to use for publishing a message.
/// Implementations consult per-type mappings configured via
/// <c>IBusConfigurator.MapSerializer&lt;TMessage, TSerializer&gt;()</c> and fall back to the
/// default serializer when no mapping is registered for the type.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe after construction. The recommended pattern is to
/// build an immutable mapping at construction time (read-only dictionary of resolved singletons)
/// so that <see cref="Resolve{T}"/> can be called from multiple threads without locking.
/// </remarks>
public interface ISerializerResolver
{
    /// <summary>
    /// Returns the serializer registered for message type <typeparamref name="T"/>,
    /// or the default serializer when no explicit mapping was configured.
    /// </summary>
    /// <typeparam name="T">The message type being published.</typeparam>
    /// <returns>
    /// The <see cref="IMessageSerializer"/> to use for serializing messages of type <typeparamref name="T"/>.
    /// Never returns <see langword="null"/>.
    /// </returns>
    IMessageSerializer Resolve<T>() where T : class;
}
