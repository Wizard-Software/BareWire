using System.Globalization;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Narrow publish abstraction used by <see cref="KafkaRetryDlqProducer"/> so the republication
/// logic can be unit-tested without a live broker (D4). In production this is backed by the
/// shared idempotent <c>IProducer</c> of <c>KafkaTransportAdapter</c> (D3); in tests it is a mock
/// capturing the produced <see cref="OutboundMessage"/>.
/// </summary>
internal interface IRetryDlqPublisher
{
    /// <summary>Publishes a single <see cref="OutboundMessage"/> to its <see cref="OutboundMessage.RoutingKey"/> topic.</summary>
    Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Builds and publishes the republication of a failed inbound message onto the retry-topic or
/// DLQ-topic (R1.3). Stamps the BareWire tracking headers and increments the retry count.
/// </summary>
/// <remarks>
/// <para>
/// <b>Body copy (D3, ADR-003 deviation):</b> <see cref="InboundMessage.Body"/> is a
/// <see cref="System.Buffers.ReadOnlySequence{T}"/> but <see cref="OutboundMessage.Body"/> is a
/// <see cref="ReadOnlyMemory{T}"/>; republication therefore copies via <c>ToArray()</c>. The
/// republish path is a failure path, not the hot path, and is excluded from the publish allocation
/// budget (mirrors the producer-side deviation documented in R1.1).
/// </para>
/// <para>
/// <b>Trust boundary (SEC-1):</b> the caller is responsible for clamping the wire
/// <c>BW-RetryCount</c> before deciding to republish; this producer increments the (already
/// clamped) count it is given.
/// </para>
/// </remarks>
internal sealed class KafkaRetryDlqProducer
{
    // ── BareWire tracking headers (R1.3) ──────────────────────────────────────
    internal const string RetryCountHeader = "BW-RetryCount";
    internal const string RetryAtHeader = "BW-RetryAt";
    internal const string OriginalTopicHeader = "BW-OriginalTopic";
    internal const string DeadLetteredHeader = "BW-DeadLettered";
    internal const string DeadLetterReasonHeader = "BW-DeadLetterReason";
    internal const string ContentTypeHeader = "BW-ContentType";

    // Headers that must not be re-emitted verbatim onto the republished message and are always
    // re-stamped by the producer itself:
    //  - BW-ConsumerId/Topic/Partition: authoritative source-delivery values (R1.2 D5); the
    //    downstream consumer re-stamps its own.
    //  - BW-DeadLettered/BW-DeadLetterReason: internal-only (SEC §14.3); the DLQ path re-stamps
    //    them so a residual/spoofed wire value cannot leak onto a republished message.
    //  - BW-RetryCount/BW-RetryAt: re-stamped by the retry path; cleared here so the DLQ path
    //    never carries a stale retry timestamp.
    private static readonly string[] HeadersStrippedBeforeRepublish =
        ["BW-ConsumerId", "BW-Topic", "BW-Partition", DeadLetteredHeader, DeadLetterReasonHeader,
         RetryCountHeader, RetryAtHeader];

    private const string DefaultContentType = "application/octet-stream";

    private readonly IRetryDlqPublisher _publisher;

    internal KafkaRetryDlqProducer(IRetryDlqPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    /// <summary>Dead-letter reason values (internal-only — never read from the wire, SEC §14.3).</summary>
    internal static class DeadLetterReason
    {
        internal const string Rejected = "rejected";
        internal const string RetryExhausted = "retry-exhausted";
        internal const string NackExhausted = "nack-exhausted";
    }

    /// <summary>
    /// Republishes <paramref name="message"/> to the retry-topic, incrementing the clamped retry
    /// count and setting <c>BW-RetryAt</c> = now + backoff(newCount).
    /// </summary>
    /// <param name="message">The source inbound message.</param>
    /// <param name="sourceTopic">The topic the message was consumed from.</param>
    /// <param name="clampedRetryCount">The clamped current retry count (SEC-1).</param>
    /// <param name="options">The retry/DLQ options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal Task RepublishToRetryAsync(
        InboundMessage message,
        string sourceTopic,
        int clampedRetryCount,
        KafkaRetryDlqOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(sourceTopic);
        ArgumentNullException.ThrowIfNull(options);

        int newRetryCount = clampedRetryCount + 1;
        string retryTopic = RetryDlqTopicNamePolicy.ResolveRetryTopic(sourceTopic, options);

        TimeSpan backoff = ExponentialBackoffCalculator.ForAttempt(
            newRetryCount, options.BaseDelay, options.BackoffMultiplier, options.MaxDelay);

        Dictionary<string, string> headers = BuildBaseHeaders(message, sourceTopic);
        headers[RetryCountHeader] = newRetryCount.ToString(CultureInfo.InvariantCulture);
        headers[RetryAtHeader] = DateTimeOffset.UtcNow.Add(backoff).ToString("O", CultureInfo.InvariantCulture);

        var outbound = BuildOutbound(retryTopic, message, headers);
        return _publisher.PublishAsync(outbound, cancellationToken);
    }

    /// <summary>
    /// Republishes <paramref name="message"/> to the DLQ-topic with the given dead-letter reason.
    /// </summary>
    /// <param name="message">The source inbound message.</param>
    /// <param name="sourceTopic">The topic the message was consumed from.</param>
    /// <param name="reason">The dead-letter reason (use <see cref="DeadLetterReason"/> constants).</param>
    /// <param name="options">The retry/DLQ options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal Task RepublishToDlqAsync(
        InboundMessage message,
        string sourceTopic,
        string reason,
        KafkaRetryDlqOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(sourceTopic);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        ArgumentNullException.ThrowIfNull(options);

        string dlqTopic = RetryDlqTopicNamePolicy.ResolveDlqTopic(sourceTopic, options);

        Dictionary<string, string> headers = BuildBaseHeaders(message, sourceTopic);
        headers[DeadLetteredHeader] = "true";
        headers[DeadLetterReasonHeader] = reason;

        var outbound = BuildOutbound(dlqTopic, message, headers);
        return _publisher.PublishAsync(outbound, cancellationToken);
    }

    /// <summary>
    /// Copies the message headers, stripping source-delivery headers and stamping
    /// <c>BW-OriginalTopic</c> (preserved across successive republications, D5).
    /// </summary>
    private static Dictionary<string, string> BuildBaseHeaders(InboundMessage message, string sourceTopic)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> header in message.Headers)
        {
            headers[header.Key] = header.Value;
        }

        // Strip the headers that are always re-stamped by the producer (source-delivery + the
        // internal dead-letter/retry headers) so no residual/spoofed wire value leaks through.
        foreach (string strippedHeader in HeadersStrippedBeforeRepublish)
        {
            headers.Remove(strippedHeader);
        }

        // BW-OriginalTopic: set once on the first republication; preserved on subsequent ones.
        if (!headers.ContainsKey(OriginalTopicHeader))
        {
            headers[OriginalTopicHeader] = sourceTopic;
        }

        return headers;
    }

    /// <summary>
    /// Builds the <see cref="OutboundMessage"/>. Carries <c>ContentType</c> from the
    /// <c>BW-ContentType</c> header when present (the inbound message has no ContentType property),
    /// falling back to <c>application/octet-stream</c> so the required ctor arg is never null (SEC §10.3).
    /// </summary>
    private static OutboundMessage BuildOutbound(
        string targetTopic, InboundMessage message, Dictionary<string, string> headers)
    {
        string contentType = headers.TryGetValue(ContentTypeHeader, out string? ct) && !string.IsNullOrEmpty(ct)
            ? ct
            : DefaultContentType;

        // D3/ADR-003 deviation: ReadOnlySequence<byte> → byte[] copy on the (cold) republish path.
        // Use BuffersExtensions.ToArray to disambiguate from LINQ/ImmutableArray ToArray overloads.
        ReadOnlyMemory<byte> body = message.Body.IsEmpty
            ? ReadOnlyMemory<byte>.Empty
            : System.Buffers.BuffersExtensions.ToArray(message.Body);

        return new OutboundMessage(
            routingKey: targetTopic,
            headers: headers,
            body: body,
            contentType: contentType);
    }
}
