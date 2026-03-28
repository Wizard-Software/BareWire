namespace BareWire.Abstractions.Routing;

/// <summary>
/// Resolves the AMQP routing key for a given message type.
/// When no explicit mapping is configured, falls back to the CLR full type name.
/// </summary>
/// <remarks>
/// Register custom mappings via <see cref="Configuration.IRabbitMqConfigurator.MapRoutingKey{T}"/>
/// to override the default <c>typeof(T).FullName</c> routing key. This is required when using
/// topic exchanges with pattern-based bindings (e.g. <c>order.*</c>).
/// </remarks>
public interface IRoutingKeyResolver
{
    /// <summary>
    /// Returns the routing key for message type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The message type to resolve the routing key for.</typeparam>
    /// <returns>
    /// The explicitly mapped routing key if one was registered via
    /// <see cref="Configuration.IRabbitMqConfigurator.MapRoutingKey{T}"/>;
    /// otherwise <c>typeof(T).FullName</c>.
    /// </returns>
    string Resolve<T>() where T : class;
}
