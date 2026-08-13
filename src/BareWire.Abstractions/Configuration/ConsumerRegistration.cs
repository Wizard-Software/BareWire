namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Captures a consumer type and the message type it handles, bridging the gap between
/// transport-level endpoint configuration (e.g. <c>e.Consumer&lt;TConsumer, TMessage&gt;()</c>)
/// and the core consume loop that needs both types to deserialize and dispatch.
/// Optionally carries a set of topic patterns the consumer listens on, a flag opting the
/// consumer into receiving messages that arrive without type metadata, and a flag opting the
/// consumer into per-consumer MassTransit envelope (de)serialization.
/// </summary>
/// <param name="ConsumerType">The concrete consumer type that handles the message.</param>
/// <param name="MessageType">The message type the consumer deserializes and processes.</param>
/// <param name="RoutingKeys">
/// The set of topic patterns this consumer listens on, used to select the consumer at dispatch
/// time. Patterns follow AMQP topic semantics where <c>*</c> matches exactly one word, <c>#</c>
/// matches zero or more words, and any other segment matches literally. A consumer may listen on
/// multiple patterns (multiple bindings). A <see langword="null"/> or empty list means the
/// consumer is a catch-all that is selected by message type alone, without any routing-key filter.
/// </param>
/// <param name="AcceptUntyped">
/// Explicit, secure-by-default opt-in (defaults to <see langword="false"/>) that makes this
/// consumer a candidate for delivering messages that arrive without type metadata — selected by
/// routing-key pattern match alone, with the raw payload deserialized into <see cref="MessageType"/>.
/// Setting this to <see langword="true"/> means the consumer may receive unauthenticated,
/// producer-controlled foreign payloads; leave it <see langword="false"/> unless the consumer is
/// intended to deliberately accept untyped foreign messages. Routing-key pattern matching is
/// client-side dispatch, NOT authorization — the trust boundary assumes broker-level publish ACLs
/// are enforced and that a schema-validation middleware validates the foreign input.
/// </param>
/// <param name="UseMassTransitEnvelope">
/// Explicit, secure-by-default opt-in (defaults to <see langword="false"/>) that selects the
/// MassTransit envelope format for this consumer's inbound deserialization and its reply
/// serialization, in preference to any per-endpoint override or the bus-global default
/// (precedence: per-consumer &gt; per-endpoint &gt; global). It is an on/off flag, not a format
/// enum. Like <paramref name="AcceptUntyped"/>, enabling it widens the trust boundary: an
/// envelope-marked consumer deserializes producer-controlled foreign payloads, so when combined
/// with <paramref name="AcceptUntyped"/> a schema-validation middleware must validate the input;
/// leave it <see langword="false"/> unless the consumer is intended to interoperate with a
/// MassTransit-enveloped producer.
/// </param>
/// <param name="ConfigureRetry">
/// Optional, default-off (defaults to <see langword="null"/>) deferred configuration of this
/// consumer's retry policy, expressed as an <see cref="Action{T}"/> over the public
/// <see cref="IRetryConfigurator"/> fluent contract. The delegate carries app-developer
/// configuration captured once at endpoint setup, not per-message input. Keeping the carrier typed
/// on the public contract — rather than an untyped handle (<c>object</c>/<c>Delegate</c>/<c>string</c>)
/// — preserves IDE discoverability of the fluent surface (<c>Interval</c>/<c>Incremental</c>/
/// <c>Exponential</c>/<c>Handle</c>/<c>Ignore</c>) while keeping <c>BareWire.Abstractions</c>
/// dependency-free: the core <c>RetryPolicy</c> type never appears here. The core materializes the
/// delegate at startup (new configurator, invoke delegate, <c>Build()</c> to a <c>RetryPolicy</c>),
/// so <c>Build()</c> stays in the core. A <see langword="null"/> value means no retry policy.
/// </param>
/// <param name="PrefetchCount">
/// Optional, default-off (defaults to <see langword="null"/>) endpoint-level prefetch limit — the
/// maximum number of unacknowledged messages the broker may deliver to this consumer before waiting
/// for settlement. A <see langword="null"/> value means the endpoint inherits its default and no
/// per-consumer override is applied. Bounds validation (rejecting non-positive values) is applied by
/// the core when this knob is materialized, not on this configuration record.
/// </param>
/// <param name="ConcurrentMessageLimit">
/// Optional, default-off (defaults to <see langword="null"/>) endpoint-level concurrency limit — the
/// maximum number of messages this consumer may process in parallel. A <see langword="null"/> value
/// means the endpoint inherits its default and no per-consumer override is applied. Bounds validation
/// (rejecting non-positive values) is applied by the core when this knob is materialized, not on this
/// configuration record.
/// </param>
public sealed record ConsumerRegistration(
    Type ConsumerType,
    Type MessageType,
    IReadOnlyList<string>? RoutingKeys = null,
    bool AcceptUntyped = false,
    bool UseMassTransitEnvelope = false,
    Action<IRetryConfigurator>? ConfigureRetry = null,
    int? PrefetchCount = null,
    int? ConcurrentMessageLimit = null);
