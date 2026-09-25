using System.Diagnostics.Metrics;

namespace BareWire.Outbox;

/// <summary>
/// Counts messages the inbox identified as duplicates — either because the lock was already held for
/// the same <c>(MessageId, ConsumerType)</c> pair, or because the entry was already marked processed.
/// </summary>
/// <remarks>
/// The only tag recorded is <see cref="ConsumerTypeTag"/>, carrying the consumer identifier the caller
/// passes to <see cref="DuplicateDetected"/> (typically the endpoint name). Cardinality is bounded only
/// as long as the caller passes a stable, low-cardinality identifier — the message id is never used as
/// a tag or otherwise recorded. The <see cref="Meter"/>, when a factory is supplied, is owned by that
/// <see cref="IMeterFactory"/> (which caches instances by name) — this type never disposes it.
/// </remarks>
internal sealed class InboxDiagnostics
{
    /// <summary>Name of the <see cref="Meter"/> instruments are created on — shared with the observability package by convention.</summary>
    internal const string MeterName = "BareWire";

    /// <summary>Name of the counter of messages skipped by the inbox because the same message was already locked or processed for the same consumer.</summary>
    internal const string DuplicatesCounterName = "barewire.inbox.duplicates";

    /// <summary>Tag key carrying the consumer identifier (typically the endpoint name) a duplicate was detected for.</summary>
    internal const string ConsumerTypeTag = "consumer_type";

    private readonly Counter<long>? _duplicates;
    private long _duplicateCount;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    /// <param name="meterFactory">
    /// The <see cref="IMeterFactory"/> the <see cref="Meter"/> is created from, or <see langword="null"/>
    /// to skip metrics entirely — <see cref="DuplicateCount"/> keeps working on its own.
    /// </param>
    internal InboxDiagnostics(IMeterFactory? meterFactory = null)
    {
        if (meterFactory is not null)
        {
            Meter meter = meterFactory.Create(MeterName);
            _duplicates = meter.CreateCounter<long>(
                DuplicatesCounterName,
                unit: "{message}",
                description: "Number of messages skipped by the inbox because the same message was already locked or processed for the same consumer.");
        }
    }

    /// <summary>Total number of duplicates detected across the lifetime of this instance, independent of the counter instrument.</summary>
    internal long DuplicateCount => Interlocked.Read(ref _duplicateCount);

    /// <summary>Records that a duplicate message was detected for <paramref name="consumerType"/>.</summary>
    /// <param name="consumerType">The consumer identifier (typically the endpoint name) the duplicate was detected for.</param>
    internal void DuplicateDetected(string consumerType)
    {
        ArgumentNullException.ThrowIfNull(consumerType);

        Interlocked.Increment(ref _duplicateCount);
        _duplicates?.Add(1, new KeyValuePair<string, object?>(ConsumerTypeTag, consumerType));
    }
}
