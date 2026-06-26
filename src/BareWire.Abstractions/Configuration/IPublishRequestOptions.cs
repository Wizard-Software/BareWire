namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Options block for the publish-style request/response mode enabled via
/// <see cref="IRabbitMqConfigurator.PublishRequest{T}(System.Action{IPublishRequestOptions})"/>.
/// The implementation of this interface is internal; only the interface is public
/// (house convention: internal implementation, public contract only).
/// All flags are opt-in and additive — message types without a <c>PublishRequest&lt;T&gt;</c>
/// registration are unaffected. All flags default to <see langword="false"/> (or
/// <see langword="null"/> for <see cref="ExchangeName"/>), consistent with the manual-topology
/// and default-OFF posture of the transport.
/// </summary>
public interface IPublishRequestOptions
{
    /// <summary>
    /// Overrides the per-type exchange name used when publishing the request.
    /// When <see langword="null"/> (the default), the transport derives the exchange name
    /// using the default <c>Namespace:TypeName</c> formatter.
    /// </summary>
    /// <value>
    /// The explicit exchange name to use, or <see langword="null"/> to fall back to the
    /// default <c>Namespace:TypeName</c> formatter. The specified exchange must be declared
    /// in the topology; fail-fast validation is performed at bus startup.
    /// </value>
    /// <remarks>
    /// <para>
    /// <strong>Security — cardinality (S1).</strong> The override value is configuration data
    /// of potentially unbounded variety. It MUST NOT be emitted as a metric dimension, log
    /// field, or <c>BareWireConfigurationException.OptionValue</c> — diagnostics must refer to
    /// a constant placeholder, not the raw override value.
    /// </para>
    /// </remarks>
    string? ExchangeName { get; set; }

    /// <summary>
    /// Enables strict mode for this request type.
    /// When <see langword="true"/>, requests fail fast when no exchange or responder
    /// is reachable, surfacing delivery failures as exceptions rather than silent drops.
    /// Default: <see langword="false"/>.
    /// </summary>
    /// <value>
    /// <see langword="true"/> to opt in to strict fail-fast behavior;
    /// <see langword="false"/> (the default) for standard fire-and-forget publish semantics.
    /// </value>
    bool Strict { get; set; }

    /// <summary>
    /// Enables automatic declaration of the per-type fanout exchange when the bus topology
    /// is deployed. When <see langword="true"/>, the transport declares the exchange derived
    /// from the default <c>Namespace:TypeName</c> formatter (or <see cref="ExchangeName"/>
    /// if set) during <c>IBusControl.DeployTopologyAsync</c>.
    /// Default: <see langword="false"/> — consistent with the manual-topology posture;
    /// the exchange must be declared explicitly via <c>ConfigureTopology</c>.
    /// </summary>
    /// <value>
    /// <see langword="true"/> to opt in to automatic per-type exchange declaration;
    /// <see langword="false"/> (the default) to require explicit topology declaration.
    /// </value>
    bool AutoDeclare { get; set; }
}
