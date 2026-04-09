namespace BareWire.Abstractions.Routing;

/// <summary>
/// Resolves the AMQP exchange name for a given message type based on explicit type→exchange mappings
/// registered via <see cref="Configuration.IRabbitMqConfigurator.MapExchange{T}"/>.
/// </summary>
/// <remarks>
/// Unlike <see cref="IRoutingKeyResolver"/>, this interface returns <see langword="null"/> when no
/// mapping is registered for the requested type. A <see langword="null"/> result means "no mapping —
/// let the <c>BW-Exchange</c> header or <c>DefaultExchange</c> decide". The precedence order is:
/// <list type="number">
///   <item><description>Explicit <c>BW-Exchange</c> header passed by the caller.</description></item>
///   <item><description>Type→exchange mapping registered via <c>MapExchange&lt;T&gt;</c> (this resolver).</description></item>
///   <item><description>Global <c>DefaultExchange</c> configured on the transport.</description></item>
///   <item><description>No exchange resolved → <see cref="Abstractions.Exceptions.BareWireConfigurationException"/> at publish time.</description></item>
/// </list>
/// The asymmetry with <see cref="IRoutingKeyResolver"/> (which always returns a non-null fallback) is
/// intentional: routing keys always have a reasonable default (<c>typeof(T).FullName</c>), whereas
/// exchange names require explicit configuration because there is no universal fallback.
/// </remarks>
public interface IExchangeResolver
{
    /// <summary>
    /// Returns the exchange name for message type <typeparamref name="T"/>, or
    /// <see langword="null"/> when no explicit mapping was registered.
    /// </summary>
    /// <typeparam name="T">The message type to resolve the exchange name for.</typeparam>
    /// <returns>
    /// The explicitly mapped exchange name if one was registered via
    /// <see cref="Configuration.IRabbitMqConfigurator.MapExchange{T}"/>;
    /// otherwise <see langword="null"/>.
    /// </returns>
    string? Resolve<T>() where T : class;
}
