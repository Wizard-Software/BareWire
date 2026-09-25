namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The reason a delivery was dropped during settlement instead of being dead-lettered or requeued. Never
/// returned to the caller of <c>SettleAsync</c> — observable only through
/// <see cref="InMemoryConsumeDiagnostics"/>'s logs and metrics.
/// </summary>
internal enum SettlementDropReason
{
    /// <summary>The source queue declares no <c>x-dead-letter-exchange</c> argument.</summary>
    NoDeadLetterExchange,

    /// <summary>Every dead-letter target queue was full or latched: none reserved a slot for the copy.</summary>
    DeadLetterQueueFull,

    /// <summary>The declared dead-letter exchange is not reachable — it matched no binding for the resolved routing key.</summary>
    Unroutable,

    /// <summary>
    /// A <c>Requeue</c> settlement reached <c>InMemoryTransportOptions.MaxRedeliveries</c> and the source
    /// queue declares no dead-letter exchange.
    /// </summary>
    MaxRedeliveries,
}

/// <summary>Converts a <see cref="SettlementDropReason"/> to its metric-tag / log-field string form.</summary>
internal static class SettlementDropReasonExtensions
{
    /// <summary>
    /// Returns the low-cardinality, allocation-free string tag for <paramref name="reason"/> — a
    /// compile-time constant per case, never built at call time.
    /// </summary>
    internal static string ToTagValue(this SettlementDropReason reason) => reason switch
    {
        SettlementDropReason.NoDeadLetterExchange => "no_dlx",
        SettlementDropReason.DeadLetterQueueFull => "dlx_full",
        SettlementDropReason.Unroutable => "unroutable",
        SettlementDropReason.MaxRedeliveries => "max_redeliveries",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown settlement drop reason."),
    };
}
