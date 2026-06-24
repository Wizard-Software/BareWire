namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Selects how per-key consumer ordering is enforced for a receive endpoint.
/// </summary>
public enum ConsumerOrderingStrategy
{
    /// <summary>
    /// Capability-driven selection (default): the library reads <see cref="TransportCapabilities"/>
    /// and the declared <see cref="IConsumerOrderingConfigurator.TransportAffinity"/>, selecting
    /// transport-native affinity when it can guarantee order, and throwing at startup when no path can
    /// (never silently degrading).
    /// </summary>
    Auto,

    /// <summary>
    /// In-process partitioned dispatch only. Guarantees order WITHIN a single instance;
    /// explicitly labelled "single-instance only" — does NOT preserve order across competing
    /// consumer instances. Must be chosen explicitly; never selected by <see cref="Auto"/>.
    /// </summary>
    LocalPartitioned,

    /// <summary>
    /// Transport-native key→consumer affinity only (e.g. Azure Service Bus <c>SessionId</c>, RabbitMQ
    /// single-active-consumer or consistent-hash exchange). Fails fast at startup when the
    /// transport/topology cannot provide it.
    /// </summary>
    TransportNative,
}
