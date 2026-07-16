namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Public, abstract base type that colocates the per-consumer settings for a typed consumer
/// <typeparamref name="TConsumer"/> in a single, discoverable place next to the consumer itself. Derive from
/// it and override
/// <see cref="Configure(IReceiveEndpointConfigurator, IConsumerConfigurator{TConsumer})"/> to compose the
/// consumer's <em>already existing</em> settings (routing keys, type-less acceptance, MassTransit-envelope
/// interop) as one grouped block. The <c>ConsumerDefinition</c> name mirrors the MassTransit convention
/// (naming parity only — no dependency on MassTransit).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Grouping, not new capability.</strong> The definition groups settings that already exist on the
/// per-consumer configurator; it does not introduce new consume-time behaviour. It is discovered by the
/// container at start-up (DI-based discovery) and materialised into the internal registration machinery by a
/// separate step — neither of which is a member of this type.
/// </para>
/// <para>
/// <strong>Design decision — abstract base class over interface.</strong> A base class lets
/// <see cref="Configure(IReceiveEndpointConfigurator, IConsumerConfigurator{TConsumer})"/> ship a default,
/// empty <see langword="virtual"/> body so a definition with no bespoke logic is a valid no-op, and leaves
/// room for future <see langword="protected"/> helpers without forcing every existing definition to
/// re-implement them. An interface would compel each implementation to re-declare any added surface, so the
/// base class is the lower-friction, more evolvable choice here.
/// </para>
/// <para>
/// <strong>Transport-agnostic — no AMQP member.</strong> This type deliberately exposes no member that names
/// the AMQP topology vocabulary (exchange / queue / binding / dead-letter). A topology helper, if offered,
/// belongs on the transport seam, never on this transport-agnostic definition living in the zero-dependency
/// abstractions package. Keeping the definition free of topology preserves the package's zero-dependency and
/// transport-neutral invariants.
/// </para>
/// <para>
/// <strong>Message type is bound later, by inference.</strong> The single type parameter is intentionally
/// constrained only to <c>class</c>: the consumer's message type is <em>not</em> bound at this type's level
/// (binding it here would make the signature reference an unconstrained type parameter and fail to compile).
/// The message type is inferred at start-up when the definition is wired to the two-parameter configurator
/// machinery; the configurator surface handed to <see cref="Configure(IReceiveEndpointConfigurator, IConsumerConfigurator{TConsumer})"/>
/// is therefore the message-agnostic façade <see cref="IConsumerConfigurator{TConsumer}"/>.
/// </para>
/// </remarks>
/// <typeparam name="TConsumer">The consumer implementation type. Must be a reference type.</typeparam>
public abstract class ConsumerDefinition<TConsumer>
    where TConsumer : class
{
    /// <summary>
    /// Applies this consumer's grouped settings. Override to declare routing keys, opt in to type-less
    /// acceptance, or opt in to the MassTransit envelope for <typeparamref name="TConsumer"/> via
    /// <paramref name="consumer"/>, and to adjust receive-endpoint-level settings via
    /// <paramref name="endpoint"/>. The default implementation is an empty no-op, so a definition that adds
    /// no bespoke configuration is still valid.
    /// </summary>
    /// <param name="endpoint">
    /// The receive endpoint the consumer is being configured on. Never <see langword="null"/>.
    /// </param>
    /// <param name="consumer">
    /// The message-agnostic per-consumer configurator façade for <typeparamref name="TConsumer"/>. Never
    /// <see langword="null"/>.
    /// </param>
    protected virtual void Configure(
        IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<TConsumer> consumer)
    {
        // Default no-op: a definition without bespoke settings is a valid, empty configuration block.
    }
}
