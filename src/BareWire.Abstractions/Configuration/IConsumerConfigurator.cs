namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Message-agnostic façade grouping the per-consumer settings that do not depend on the consumer's message
/// type, for a typed consumer <typeparamref name="TConsumer"/>. It is the single-parameter base of
/// <see cref="IConsumerConfigurator{TConsumer, TMessage}"/> — which inherits every member declared here — and
/// the configurator surface handed to
/// <see cref="ConsumerDefinition{TConsumer}.Configure(IReceiveEndpointConfigurator, IConsumerConfigurator{TConsumer})"/>,
/// where the consumer's message type is not yet bound at the type level (it is inferred at start-up).
/// </summary>
/// <remarks>
/// <para>
/// Methods return <see langword="void"/> by design, matching the house configurator convention (see
/// <see cref="IReceiveEndpointConfigurator"/> and <see cref="IPublishConfigurator{T}"/>) — settings are
/// applied imperatively inside the delegate rather than fluently chained.
/// </para>
/// <para>
/// This façade carries only message-agnostic settings and names <strong>no</strong> AMQP topology
/// vocabulary (exchange / queue / binding): declaring routing keys is a client-side dispatcher predicate,
/// <strong>not</strong> topology. Which deliveries arrive in the queue stays governed by manually declared
/// bindings (queue→exchange). A consumer that declares no routing keys is a catch-all over its message type
/// (unchanged behaviour).
/// </para>
/// </remarks>
/// <typeparam name="TConsumer">The consumer implementation type. Must be a reference type.</typeparam>
public interface IConsumerConfigurator<TConsumer>
    where TConsumer : class
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
    /// to this consumer purely by routing-key pattern match, with the raw payload deserialized to the
    /// consumer's message type (raw-first interop).
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
    /// is deserialized into the consumer's message type. Exposing this layer assumes publish
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

    /// <summary>
    /// Opts this consumer in to the MassTransit envelope interop format
    /// (<c>application/vnd.masstransit+json</c>) for both directions — <em>receive</em> and
    /// <em>reply</em> — independently of the bus-global or per-endpoint default format. A consumer that
    /// "speaks MassTransit" reads an envelope on the way in and writes an envelope on the way out, as one
    /// coherent interop mode. This is the third and narrowest axis of message-format choice
    /// (per-consumer), alongside the bus-global registration and the per-endpoint
    /// <see cref="IReceiveEndpointConfigurator"/> serializer override.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope — receive and reply.</strong> On the <em>receive</em> path the marked consumer's
    /// payload is deserialized from the MassTransit envelope: the envelope's <c>message</c> field is
    /// unwrapped into the consumer's message type, and the envelope's <c>messageType</c> (a URN) and
    /// envelope headers are mapped. On the <em>reply</em> path, a <c>RespondAsync</c> from this consumer
    /// wraps the response in a MassTransit <em>response</em> envelope with the correlating
    /// <c>requestId</c>, so request/response interop with a MassTransit peer round-trips correctly.
    /// </para>
    /// <para>
    /// <strong>Precedence — per-consumer wins.</strong> Format resolution runs narrowest-to-widest:
    /// per-consumer <c>UseMassTransitEnvelope()</c> &gt; per-endpoint
    /// <c>UseSerializer</c>/<c>UseDeserializer</c> override &gt; bus-global default (raw-first, or a
    /// globally registered envelope). A marked consumer therefore (de)serializes through the MassTransit
    /// envelope regardless of the endpoint's default deserializer; an unmarked consumer sharing the same
    /// endpoint keeps the default format. The opt-in is also an explicit declaration that "this consumer
    /// expects a MassTransit envelope", which disambiguates deliveries with an absent or ambiguous
    /// <c>content-type</c> and additionally governs the reply side that a content-type router does not
    /// cover.
    /// </para>
    /// <para>
    /// Like the other methods on this configurator, this returns <see langword="void"/> by design,
    /// matching the house configurator convention (see <see cref="AcceptUntyped"/> and
    /// <see cref="IReceiveEndpointConfigurator"/>) — the setting is applied imperatively inside the
    /// delegate rather than fluently chained. The chained variant
    /// <c>Consumer&lt;,&gt;().WithMassTransitEnvelope()</c> is deliberately not offered: it would force
    /// the configurator family away from the <see langword="void"/> convention.
    /// </para>
    /// <para>
    /// The call is <strong>idempotent</strong>: it sets an on/off flag (not a set that accumulates), so
    /// calling it more than once has the same effect as calling it once. This is a deliberate parity with
    /// <see cref="AcceptUntyped"/> and a contrast with the accumulating <see cref="RoutingKey"/> /
    /// <see cref="RoutingKeys"/> set.
    /// </para>
    /// <para>
    /// <strong>Secure-by-default — this is an explicit, conscious opt-in.</strong> The envelope format is
    /// never enabled for a consumer implicitly; a developer opts a single consumer in to it deliberately.
    /// This method is <em>orthogonal</em> to the routing-key dispatch axis
    /// (<see cref="RoutingKey"/>/<see cref="RoutingKeys"/>/<see cref="AcceptUntyped"/>): routing keys
    /// select <strong>which</strong> consumer handles a delivery, whereas this opt-in selects
    /// <strong>how</strong> that consumer's payload is (de)serialized and whether its reply is wrapped.
    /// The two may coexist in the same configuration block. Where envelope opt-in is combined with an
    /// untrusted-input axis (a consumer also marked <see cref="AcceptUntyped"/> for foreign JSON), the
    /// bus surfaces a startup warning when a schema-validation middleware is absent, preserving the
    /// secure-by-default posture.
    /// </para>
    /// </remarks>
    void UseMassTransitEnvelope();

    /// <summary>
    /// Configures this consumer's retry policy through the public <see cref="IRetryConfigurator"/> fluent
    /// contract. The delegate is invoked to build the policy (for example
    /// <c>r =&gt; r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 2.0)</c>); it is stored
    /// verbatim and materialized to a concrete retry policy later in the core, so no core policy type leaks
    /// into this zero-dependency abstraction.
    /// </summary>
    /// <remarks>
    /// The call is a <strong>scalar knob — last call wins</strong> (unlike the accumulating
    /// <see cref="RoutingKey"/>/<see cref="RoutingKeys"/> set). Not calling it leaves the consumer's retry
    /// behaviour unchanged (the endpoint-level default). It is the ergonomic composition point for retry on a
    /// <see cref="ConsumerDefinition{TConsumer}"/>: <c>consumer.Retry(r =&gt; r.Interval(3, delay))</c>.
    /// </remarks>
    /// <param name="configure">The retry-configuration delegate. Must not be <see langword="null"/>.</param>
    void Retry(Action<IRetryConfigurator> configure);
}

/// <summary>
/// Configures consume-time routing-key dispatch for a typed consumer <typeparamref name="TConsumer"/>
/// handling message type <typeparamref name="TMessage"/>, as a single grouped, discoverable block. An
/// instance is passed to the delegate supplied to the grouped <c>Consumer&lt;TConsumer, TMessage&gt;</c>
/// configuration overload on <see cref="IReceiveEndpointConfigurator"/>. The declared routing keys are a set
/// of AMQP topic patterns matched <em>client-side at dispatch</em> against the delivery's routing key — a
/// dispatcher predicate that selects which of several consumers sharing a queue handles a given delivery.
/// This is the consume-side ergonomic counterpart of the publish-side per-type routing configurator
/// (<see cref="IPublishConfigurator{T}"/>) with deliberately different semantics (a set of match patterns,
/// not a single produced key).
/// </summary>
/// <remarks>
/// This two-parameter form inherits every (message-agnostic) member from the single-parameter façade
/// <see cref="IConsumerConfigurator{TConsumer}"/>; the current four settings are all message-agnostic and
/// therefore live on the façade. The second type parameter binds the consumer's message type at the type
/// level and reserves this form for future members that genuinely require <typeparamref name="TMessage"/>.
/// The change is additive and non-breaking: existing callers of the four inherited methods continue to see
/// them unchanged through inheritance.
/// </remarks>
/// <typeparam name="TConsumer">
/// The consumer implementation type. Must implement <see cref="IConsumer{TMessage}"/>.
/// </typeparam>
/// <typeparam name="TMessage">The message type this consumer handles. Must be a reference type.</typeparam>
public interface IConsumerConfigurator<TConsumer, TMessage> : IConsumerConfigurator<TConsumer>
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : class
{
    // The four message-agnostic settings (RoutingKey, RoutingKeys, AcceptUntyped, UseMassTransitEnvelope)
    // are inherited from IConsumerConfigurator<TConsumer>. This form is reserved for future members that
    // require TMessage bound at the type level.
}
