using System.Diagnostics;

namespace BareWire.Abstractions.Observability;

/// <summary>
/// Facade for all BareWire observability operations: distributed tracing, metrics recording,
/// and W3C trace context propagation. Inject this interface into Core components to enable
/// OpenTelemetry instrumentation without coupling Core to the OTel SDK.
/// </summary>
/// <remarks>
/// When <c>BareWire.Observability</c> is not registered, a no-op
/// <c>NullInstrumentation</c> implementation is used automatically —
/// Core operates normally with zero observability overhead.
/// Implementations must be thread-safe.
/// </remarks>
public interface IBareWireInstrumentation
{
    /// <summary>
    /// Starts a producer span for a publish operation.
    /// The returned <see cref="Activity"/> must be disposed by the caller when the operation completes.
    /// Returns <see langword="null"/> when no listener is attached to the activity source.
    /// </summary>
    /// <param name="messageType">The fully-qualified or short message type name (e.g. <c>OrderCreated</c>).</param>
    /// <param name="destination">The target exchange or topic name.</param>
    /// <param name="messageId">The unique identifier of the message being published.</param>
    /// <returns>A started <see cref="Activity"/> with kind <c>Producer</c>, or <see langword="null"/>.</returns>
    Activity? StartPublishActivity(string messageType, string destination, Guid messageId);

    /// <summary>
    /// Starts a consumer span for a consume operation, restoring the distributed trace context
    /// from the inbound message headers.
    /// The returned <see cref="Activity"/> must be disposed by the caller when the operation completes.
    /// Returns <see langword="null"/> when no listener is attached to the activity source.
    /// </summary>
    /// <param name="messageType">The fully-qualified or short message type name (e.g. <c>OrderCreated</c>).</param>
    /// <param name="endpoint">The receive endpoint or queue name.</param>
    /// <param name="messageId">The unique identifier of the message being consumed.</param>
    /// <param name="headers">
    /// The inbound message headers from which trace context (<c>traceparent</c>, <c>tracestate</c>) is extracted.
    /// </param>
    /// <returns>A started <see cref="Activity"/> with kind <c>Consumer</c>, or <see langword="null"/>.</returns>
    Activity? StartConsumeActivity(
        string messageType,
        string endpoint,
        Guid messageId,
        IReadOnlyDictionary<string, string> headers);

    /// <summary>
    /// Starts an internal span for a SAGA state transition.
    /// The returned <see cref="Activity"/> must be disposed by the caller when the transition completes.
    /// Returns <see langword="null"/> when no listener is attached to the activity source.
    /// </summary>
    /// <param name="sagaType">The SAGA type name (e.g. <c>OrderSaga</c>).</param>
    /// <param name="stateFrom">The state the SAGA is transitioning from.</param>
    /// <param name="stateTo">The state the SAGA is transitioning to.</param>
    /// <param name="correlationId">The SAGA instance correlation identifier.</param>
    /// <returns>A started <see cref="Activity"/> with kind <c>Internal</c>, or <see langword="null"/>.</returns>
    Activity? StartSagaTransitionActivity(string sagaType, string stateFrom, string stateTo, Guid correlationId);

    /// <summary>
    /// Records a successful publish: increments the <c>barewire.messages.published</c> counter
    /// and records a sample in the <c>barewire.message.size</c> histogram.
    /// </summary>
    /// <param name="endpoint">The target exchange or topic name.</param>
    /// <param name="messageType">The fully-qualified or short message type name.</param>
    /// <param name="messageSize">The serialized payload size in bytes.</param>
    void RecordPublish(string endpoint, string messageType, int messageSize);

    /// <summary>
    /// Records a successful consume: increments the <c>barewire.messages.consumed</c> counter
    /// and records samples in the <c>barewire.message.duration</c> and <c>barewire.message.size</c> histograms.
    /// </summary>
    /// <param name="endpoint">The receive endpoint or queue name.</param>
    /// <param name="messageType">The fully-qualified or short message type name.</param>
    /// <param name="durationMs">The end-to-end handler processing time in milliseconds.</param>
    /// <param name="messageSize">The serialized payload size in bytes.</param>
    void RecordConsume(string endpoint, string messageType, double durationMs, int messageSize);

    /// <summary>
    /// Records a processing failure: increments the <c>barewire.messages.failed</c> counter.
    /// </summary>
    /// <param name="endpoint">The receive endpoint or queue name where the failure occurred.</param>
    /// <param name="messageType">The fully-qualified or short message type name.</param>
    /// <param name="errorType">
    /// The exception type name or a short error category (e.g. <c>InvalidOperationException</c>,
    /// <c>DeserializationError</c>).
    /// </param>
    void RecordFailure(string endpoint, string messageType, string errorType);

    /// <summary>
    /// Records a dead-letter event: increments the <c>barewire.messages.dead_lettered</c> counter.
    /// Call this when a message is moved to the dead-letter queue after all retries are exhausted.
    /// </summary>
    /// <param name="endpoint">The source endpoint from which the message was dead-lettered.</param>
    /// <param name="messageType">The fully-qualified or short message type name.</param>
    void RecordDeadLetter(string endpoint, string messageType);

    /// <summary>
    /// Adjusts the in-flight message gauge (<c>barewire.messages.inflight</c>) and
    /// the in-flight bytes gauge (<c>barewire.messages.inflight.bytes</c>) by the given deltas.
    /// Pass a positive <paramref name="delta"/> when a message enters the pipeline
    /// and a negative delta when it leaves.
    /// </summary>
    /// <param name="endpoint">The receive endpoint or queue name.</param>
    /// <param name="messageType">The fully-qualified or short message type name.</param>
    /// <param name="delta">The change in message count (+1 on receive, -1 on settle).</param>
    /// <param name="bytesDelta">The change in bytes (+N on receive, -N on settle).</param>
    void RecordInflight(string endpoint, string messageType, int delta, int bytesDelta);

    /// <summary>
    /// Adjusts the publish-pending gauge (<c>barewire.publish.pending</c>) by the given delta.
    /// Reflects the number of messages queued in the bounded outgoing channel awaiting dispatch.
    /// Pass a positive <paramref name="delta"/> when a message is enqueued and a negative delta
    /// when it is dequeued.
    /// </summary>
    /// <param name="endpoint">The target exchange or topic name.</param>
    /// <param name="delta">The change in pending publish count.</param>
    void RecordPublishPending(string endpoint, int delta);

    /// <summary>
    /// Records a rejected publish: increments the <c>barewire.publish.rejected</c> counter.
    /// Call this when <c>BoundedChannelFullMode.DropWrite</c> drops a message because
    /// the outgoing channel is at capacity.
    /// </summary>
    /// <param name="endpoint">The target exchange or topic name.</param>
    /// <param name="messageType">The fully-qualified or short message type name.</param>
    void RecordPublishRejected(string endpoint, string messageType);

    /// <summary>
    /// Injects the W3C trace context (<c>traceparent</c>, <c>tracestate</c>) from the given
    /// <see cref="Activity"/> into the provided outbound message headers dictionary.
    /// Safe to call with a <see langword="null"/> activity — writes nothing in that case.
    /// </summary>
    /// <param name="activity">
    /// The currently active span, typically obtained from <see cref="StartPublishActivity"/>.
    /// May be <see langword="null"/> when no listener is attached.
    /// </param>
    /// <param name="headers">
    /// The mutable outbound message headers dictionary to inject the trace context into.
    /// </param>
    void InjectTraceContext(Activity? activity, IDictionary<string, string> headers);

    /// <summary>
    /// Extracts the W3C trace context from inbound message headers and starts a new
    /// <see cref="Activity"/> as a child of the propagated trace.
    /// Returns <see langword="null"/> when no listener is attached to the activity source.
    /// If no <c>traceparent</c> header is present, starts a new root span.
    /// </summary>
    /// <param name="headers">The inbound message headers to extract trace context from.</param>
    /// <param name="operationName">The span name for the extracted activity.</param>
    /// <returns>A started child <see cref="Activity"/>, or <see langword="null"/>.</returns>
    Activity? ExtractTraceContext(IReadOnlyDictionary<string, string> headers, string operationName);
}
