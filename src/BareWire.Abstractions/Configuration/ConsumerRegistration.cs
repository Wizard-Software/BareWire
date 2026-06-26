namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Captures a consumer type and the message type it handles, bridging the gap between
/// transport-level endpoint configuration (e.g. <c>e.Consumer&lt;TConsumer, TMessage&gt;()</c>)
/// and the core consume loop that needs both types to deserialize and dispatch.
/// Optionally carries a set of topic patterns the consumer listens on and a flag opting the
/// consumer into receiving messages that arrive without type metadata.
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
/// intended to deliberately accept untyped foreign messages.
/// </param>
public sealed record ConsumerRegistration(
    Type ConsumerType,
    Type MessageType,
    IReadOnlyList<string>? RoutingKeys = null,
    bool AcceptUntyped = false);
