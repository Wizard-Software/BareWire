namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Read-only view of the per-key consumer-ordering settings collected through
/// <see cref="IConsumerOrderingConfigurator"/>. Exposed on <see cref="EndpointBinding.Ordering"/> so the
/// transport-agnostic dispatch engine (<c>ReceiveEndpointRunner</c> in the core <c>BareWire</c> package)
/// can read ordering configuration <strong>without downcasting</strong> to a transport-local carrier type.
/// </summary>
/// <remarks>
/// <para>
/// The core dispatch engine never references the concrete carrier types — each transport package defines
/// its own internal carrier (the core <c>ConsumerOrderingConfiguration</c> and, for example, the RabbitMQ
/// package-local <c>OrderingConfiguration</c>), and they share no common base because the package
/// dependency rules forbid a transport package from referencing the core. A downcast such as
/// <c>(ConsumerOrderingConfiguration)binding.Ordering</c> would therefore always be <see langword="null"/>
/// for a non-core binding, silently disabling the ordered path. This interface is the single carrier the
/// engine reads through, which both carriers implement.
/// </para>
/// <para>
/// This is the read side of the write-side fluent <see cref="IConsumerOrderingConfigurator"/>: the
/// configurator collects settings during configuration, and this interface exposes the collected values
/// at runtime. Per-key ordering is OFF by default — <see cref="EndpointBinding.Ordering"/> is
/// <see langword="null"/> when no <c>OrderedBy</c> call was made.
/// </para>
/// </remarks>
public interface IConsumerOrderingConfiguration
{
    /// <summary>
    /// Gets the header name to read the ordering key from, or <see langword="null"/> when the key source
    /// is not a header.
    /// </summary>
    string? HeaderName { get; }

    /// <summary>
    /// Gets the typed selector that projects a message to its ordering key, or <see langword="null"/> when
    /// the key source is not a selector.
    /// </summary>
    Delegate? Selector { get; }

    /// <summary>
    /// Gets the message type the <see cref="Selector"/> reads, or <see langword="null"/> when no selector
    /// is configured.
    /// </summary>
    Type? SelectorMessageType { get; }

    /// <summary>
    /// Gets a value indicating whether the auto-stamped correlation-id is used as the ordering key.
    /// </summary>
    bool UseCorrelationId { get; }

    /// <summary>
    /// Gets the configured cross-key concurrency (number of fixed lanes), or <see langword="null"/> when
    /// not set explicitly. When unset, the dispatch engine falls back to
    /// <see cref="IReceiveEndpointConfigurator.ConcurrentMessageLimit"/>.
    /// </summary>
    int? Concurrency { get; }

    /// <summary>Gets the configured ordering strategy. Default <see cref="ConsumerOrderingStrategy.Auto"/>.</summary>
    ConsumerOrderingStrategy Strategy { get; }

    /// <summary>Gets the declared transport-native affinity. Default <see cref="TransportAffinity.None"/>.</summary>
    TransportAffinity TransportAffinity { get; }

    /// <summary>Gets the per-key maximum delivery attempts before park/DLQ. Default <c>0</c> (disabled).</summary>
    int MaxDeliveryAttempts { get; }
}
