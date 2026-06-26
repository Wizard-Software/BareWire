namespace BareWire.Transport.RabbitMQ.Internal;

/// <summary>
/// Produces the RabbitMQ exchange name for a given message type using the
/// <c>Namespace:TypeName</c> convention.
///
/// <para>
/// The format is <c>{typeof(T).Namespace}:{typeof(T).Name}</c> — a literal colon
/// (<c>:</c>) separator between the CLR namespace and the type's simple PascalCase name.
/// Dots within the namespace are preserved as-is; the type name is the CLR
/// <c>Type.Name</c>, which preserves PascalCase.
/// Examples:
/// <list type="bullet">
///   <item><description><c>OrderSystem.Events:OrderSubmitted</c></description></item>
///   <item><description><c>A.B.C:DeepNamespaceEvent</c></description></item>
/// </list>
/// </para>
///
/// <para>
/// The formatted string is computed once per closed generic type and stored in a
/// per-type static field via the nested <c>Cache&lt;T&gt;</c> class. Subsequent calls to
/// <see cref="Format{T}"/> return the same <see cref="string"/> instance with zero
/// allocation, matching the <c>UrnCache&lt;T&gt;</c> pattern used in
/// <c>MassTransitEnvelopeSerializer</c>.
/// </para>
///
/// <para>
/// <strong>Local reimplementation — layer rule.</strong>
/// <c>BareWire.Transport.RabbitMQ</c> depends only on <c>BareWire.Abstractions</c>;
/// it must not take a dependency on any interop or serialization assembly.
/// The <c>Namespace:TypeName</c> convention is therefore reimplemented locally here to
/// preserve that dependency boundary (enforced by NetArchTest in task 14.15).
/// </para>
///
/// <para>
/// <strong>Limitation D6 (MVP).</strong>
/// Only simple, non-nested types are supported. Generic types produce CLR backtick
/// notation (e.g. <c>List`1</c>) and nested types produce a plus sign
/// (e.g. <c>Outer+Inner</c>). Both cases are unsupported by this formatter and require
/// an explicit <c>o.ExchangeName</c> override (task 14.5).
/// </para>
/// </summary>
internal static class RequestExchangeNameFormatter
{
    // Cache the formatted string per closed generic type argument.
    // The CLR initialises each Cache<T>.Value exactly once, lazily, in a thread-safe
    // manner — no locking required on the call path.
    private static class Cache<T>
    {
        internal static readonly string Value = $"{typeof(T).Namespace}:{typeof(T).Name}";
    }

    /// <summary>
    /// Returns the exchange name for message type <typeparamref name="T"/>.
    /// The result is cached per type; repeated calls return the same string instance.
    /// </summary>
    /// <typeparam name="T">The message type. Must be a reference type.</typeparam>
    /// <returns>
    /// A string in the form <c>Namespace:TypeName</c>, e.g.
    /// <c>OrderSystem.Events:OrderSubmitted</c>.
    /// </returns>
    internal static string Format<T>() where T : class => Cache<T>.Value;
}
