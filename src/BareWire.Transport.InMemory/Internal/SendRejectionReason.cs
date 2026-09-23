namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The reason a message, or one fan-out copy of a message, was not accepted by
/// <see cref="InMemorySender.SendAsync"/>. Never returned to the caller of <c>SendBatchAsync</c> — it is
/// only observable through <see cref="InMemorySendDiagnostics"/>'s logs and metrics.
/// </summary>
internal enum SendRejectionReason
{
    /// <summary>The target queue was full: latched, or full without room freeing up within the wait budget.</summary>
    QueueFull,

    /// <summary>The caller's <see cref="CancellationToken"/> was cancelled while the call's one wait was pending.</summary>
    Cancelled,

    /// <summary>The adapter was disposed before or while this message was processed.</summary>
    Closed,

    /// <summary>The message body exceeds <c>InMemoryTransportOptions.MaxMessageSize</c>.</summary>
    Oversized,

    /// <summary>The resolved exchange is neither the default exchange nor declared in the topology.</summary>
    UndeclaredExchange,

    /// <summary>No exchange header was supplied and no <c>InMemoryTransportOptions.DefaultExchange</c> is configured.</summary>
    NoExchange,

    /// <summary>The routing key exceeds 255 UTF-8 bytes, the AMQP limit for a binding key.</summary>
    RoutingKeyTooLong,

    /// <summary>An unexpected exception was thrown while processing this message.</summary>
    InternalError,
}

/// <summary>Converts a <see cref="SendRejectionReason"/> to its metric-tag / log-field string form.</summary>
internal static class SendRejectionReasonExtensions
{
    /// <summary>
    /// Returns the low-cardinality, allocation-free string tag for <paramref name="reason"/> (a
    /// compile-time constant per case, never built at call time).
    /// </summary>
    internal static string ToTag(this SendRejectionReason reason) => reason switch
    {
        SendRejectionReason.QueueFull => "queue_full",
        SendRejectionReason.Cancelled => "cancelled",
        SendRejectionReason.Closed => "closed",
        SendRejectionReason.Oversized => "oversized",
        SendRejectionReason.UndeclaredExchange => "undeclared_exchange",
        SendRejectionReason.NoExchange => "no_exchange",
        SendRejectionReason.RoutingKeyTooLong => "routing_key_too_long",
        SendRejectionReason.InternalError => "internal_error",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown send rejection reason."),
    };
}
