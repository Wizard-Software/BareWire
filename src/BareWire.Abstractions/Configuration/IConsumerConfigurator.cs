namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Configures consume-time routing-key dispatch for a typed consumer
/// <typeparamref name="TConsumer"/> handling message type <typeparamref name="TMessage"/>, as a single
/// grouped, discoverable block. An instance is passed to the delegate supplied to the grouped
/// <c>Consumer&lt;TConsumer, TMessage&gt;</c> configuration overload on
/// <see cref="IReceiveEndpointConfigurator"/>. The declared routing keys are a set of AMQP topic
/// patterns matched <em>client-side at dispatch</em> against the delivery's routing key — a dispatcher
/// predicate that selects which of several consumers sharing a queue handles a given delivery. This is
/// the consume-side ergonomic counterpart of the publish-side per-type routing configurator
/// (<see cref="IPublishConfigurator{T}"/>) with deliberately different semantics (a set of match
/// patterns, not a single produced key).
/// </summary>
/// <remarks>
/// <para>
/// Methods return <see langword="void"/> by design, matching the house configurator convention (see
/// <see cref="IReceiveEndpointConfigurator"/> and <see cref="IPublishConfigurator{T}"/>) — settings are
/// applied imperatively inside the delegate rather than fluently chained.
/// </para>
/// <para>
/// This is a dispatcher predicate, <strong>not</strong> topology: which deliveries arrive in the queue
/// stays governed by manually declared bindings (queue→exchange). Declaring routing keys does not create
/// or alter any binding. A consumer that declares no routing keys is a catch-all over its message type
/// (unchanged behaviour).
/// </para>
/// </remarks>
/// <typeparam name="TConsumer">
/// The consumer implementation type. Must implement <see cref="IConsumer{TMessage}"/>.
/// </typeparam>
/// <typeparam name="TMessage">The message type this consumer handles. Must be a reference type.</typeparam>
public interface IConsumerConfigurator<TConsumer, TMessage>
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : class
{
    /// <summary>
    /// Adds a single AMQP topic pattern to this consumer's routing-key set (sugar for a one-pattern
    /// <see cref="RoutingKeys"/> call). Parity with <see cref="IPublishConfigurator{T}.RoutingKey"/>.
    /// The pattern is matched against the delivery's routing key at dispatch; <c>*</c> matches exactly
    /// one word, <c>#</c> matches zero or more words, <c>.</c> is the word separator, and a pattern
    /// without wildcards is matched as literal equality.
    /// </summary>
    /// <remarks>
    /// <strong>Accumulation semantics</strong> (a deliberate deviation from the last-call-wins rule of the
    /// publish-side per-type routing): each call <em>adds</em> the pattern to the consumer's set rather than
    /// replacing it (duplicates are idempotent). A consumer may legitimately listen on many keys — like a
    /// queue with multiple bindings — so repeated calls accumulate. (Last-call-wins applies to the publish
    /// side, where a message carries a single concrete key.)
    /// </remarks>
    /// <param name="routingKey">
    /// The AMQP topic pattern to add. Must not be <see langword="null"/> or empty.
    /// </param>
    void RoutingKey(string routingKey);

    /// <summary>
    /// Adds multiple AMQP topic patterns to this consumer's routing-key set in one call. Each pattern
    /// follows the same topic semantics as <see cref="RoutingKey"/> (<c>*</c> = one word, <c>#</c> = zero
    /// or more words, <c>.</c> separator, exact = literal equality).
    /// </summary>
    /// <remarks>
    /// Accumulates into the same set as <see cref="RoutingKey"/> (duplicates idempotent); calls do not
    /// overwrite prior patterns. See <see cref="RoutingKey"/> for the rationale behind accumulation.
    /// </remarks>
    /// <param name="routingKeys">
    /// The AMQP topic patterns to add. Each entry must not be <see langword="null"/> or empty.
    /// </param>
    void RoutingKeys(params string[] routingKeys);

    /// <summary>
    /// Opts this consumer in to the type-less dispatch layer: deliveries whose message type cannot be
    /// resolved (foreign / raw JSON with no BareWire message-type header) become eligible to be dispatched
    /// to this consumer purely by routing-key pattern match, with the raw payload deserialized to
    /// <typeparamref name="TMessage"/> (raw-first interop).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Secure-by-default — this is an explicit, mandatory opt-in.</strong> Without calling this
    /// method, declared routing keys narrow the <em>typed</em> dispatch path only; the consumer is never a
    /// candidate for type-less deliveries and never becomes a silent sink for untrusted foreign JSON. The
    /// delivery's routing key is unauthenticated, producer-controlled input, so exposing a consumer to
    /// type-less deserialization must be a conscious decision.
    /// </para>
    /// <para>
    /// <strong>Trust boundary — broker-level publish ACL is assumed.</strong> Routing-key pattern
    /// matching is performed <em>client-side at dispatch</em> and is a dispatcher predicate, NOT an
    /// authorization mechanism: an attacker who can publish to the bound exchange fully controls the
    /// delivery's routing key and payload, and therefore which type-less consumer is selected and what
    /// is deserialized into <typeparamref name="TMessage"/>. Exposing this layer assumes publish
    /// permissions are enforced at the broker (e.g. RabbitMQ publish ACL / vhost permissions) and
    /// that a schema-validation middleware validates the foreign-input axis (routing key + broker
    /// identity + payload shape/size). The bus emits a startup warning when an
    /// <c>AcceptUntyped()</c> endpoint is configured without such a middleware.
    /// </para>
    /// <para>
    /// The call is <strong>idempotent</strong>: it sets an on/off flag (not a set that accumulates), so
    /// calling it more than once has the same effect as calling it once.
    /// </para>
    /// </remarks>
    void AcceptUntyped();
}
