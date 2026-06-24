using BareWire.Abstractions.Configuration;
using BareWire.Pipeline;

namespace BareWire.Bus;

/// <summary>
/// Resolves an ordering key from a message's headers given an
/// <see cref="IConsumerOrderingConfiguration"/>, and maps that key to a fixed lane index via
/// stable hashing (SHA-256 via <see cref="GuidHelper.ParseOrHash"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Fixed-lane hashing (LocalPartitioned):</strong> The same key value always maps to the
/// same lane within a single process instance. Distinct keys may share a lane (partition model,
/// analogous to Kafka), but a given key never crosses lanes — this gives per-key FIFO ordering
/// on the in-order <see cref="System.Threading.Channels.Channel{T}"/> maintained by each lane.
/// </para>
/// <para>
/// <strong>Default lane count (D2 pin):</strong> 8 fixed lanes by default —
/// <c>EndpointBinding.ConcurrentMessageLimit</c> defaults to 8, so when the caller has not
/// explicitly set <c>Concurrency(n)</c>, <c>ResolveLaneCount()</c> returns 8.
/// Operators experiencing key-skew (hot keys monopolising a lane) should raise N to redistribute
/// load across more lanes.
/// </para>
/// <para>
/// <strong>Single-instance semantics only:</strong>
/// <see cref="ResolveLaneIndex"/> uses <see cref="Guid.GetHashCode"/> on the parsed/hashed Guid,
/// which is stable within the same process but not across processes or restarts.
/// <c>LocalPartitioned</c> is explicitly single-instance — cross-instance affinity requires a
/// consistent-hash transport layer (R8.9/R8.13). Do not assume cross-process lane stability.
/// </para>
/// <para>
/// <strong>Key-skew note:</strong> With a small lane count (default 8) and a highly skewed key
/// distribution (one key carrying the majority of traffic), most messages will queue behind the
/// same lane worker. Raise <c>Concurrency(n)</c> to redistribute; hot-key mitigation (R8.12)
/// is a separate concern.
/// </para>
/// <para>
/// <strong>SEC discipline:</strong> Key values are never logged, thrown, or included in
/// diagnostic strings. The key is hashed immediately in <see cref="ResolveLaneIndex"/> and the
/// raw string value does not survive past that point (S1/S2 intent — full enforcement in R8.8).
/// </para>
/// <para>
/// <strong>Typed selector (Selector property) deferred to R8.13:</strong> Fan-out runs on the raw
/// <see cref="BareWire.Abstractions.Transport.InboundMessage"/> before deserialization; resolving
/// a CLR-property selector here would require premature deserialization that violates ADR-003
/// (zero-copy). If <see cref="IConsumerOrderingConfiguration.Selector"/> is set but no
/// <see cref="IConsumerOrderingConfiguration.HeaderName"/> is configured, R8.6 treats the message
/// as keyless (round-robin lane assignment) until R8.13 delivers the full key-source chain.
/// </para>
/// </remarks>
internal static class OrderingKeyResolver
{
    /// <summary>Canonical consumer-side correlation-id header (kebab-case).</summary>
    /// <remarks>
    /// Confirmed in <c>ConsumeContext.cs:179</c> and both transport header mappers
    /// (<c>RabbitMqHeaderMapper</c>, <c>AzureServiceBusHeaderMapper</c>).
    /// The PascalCase "CorrelationId" used by <c>PartitionerMiddleware.DefaultKeySelector</c>
    /// is a documented pre-existing latent bug — do NOT copy it here.
    /// </remarks>
    private const string CorrelationIdHeader = "correlation-id";

    /// <summary>
    /// Resolves the ordering key for a message from the configured key source.
    /// </summary>
    /// <param name="ordering">The ordering configuration for the endpoint.</param>
    /// <param name="headers">The message headers.</param>
    /// <returns>
    /// The resolved key string, or <see langword="null"/> if no key could be resolved
    /// (keyless — message will use round-robin lane assignment).
    /// </returns>
    internal static string? Resolve(
        IConsumerOrderingConfiguration ordering,
        IReadOnlyDictionary<string, string> headers)
    {
        // (a) Header source — explicit header name takes highest precedence.
        if (ordering.HeaderName is not null)
        {
            headers.TryGetValue(ordering.HeaderName, out string? headerValue);
            return headerValue; // may be null if header absent → keyless
        }

        // (b) Typed selector — DEFERRED to R8.13.
        // Fan-out operates on raw InboundMessage BEFORE deserialization; resolving a CLR-property
        // selector here requires premature deserialization, violating ADR-003 (zero-copy).
        // When only a selector is configured (no HeaderName), fall through to keyless in R8.6.
        // R8.13 delivers the full key-source chain including cross-instance M3 semantics.

        // (c) Correlation-id source.
        if (ordering.UseCorrelationId)
        {
            headers.TryGetValue(CorrelationIdHeader, out string? correlationId);
            return correlationId; // may be null if header absent → keyless
        }

        // (d) Keyless — no configured key source resolved a value.
        return null;
    }

    /// <summary>
    /// Maps a resolved ordering key (or <see langword="null"/> for keyless messages) to a lane index.
    /// </summary>
    /// <param name="key">
    /// The ordering key resolved by <see cref="Resolve"/>, or <see langword="null"/> for keyless messages.
    /// </param>
    /// <param name="arrivalSequence">The monotonic arrival sequence assigned before fan-out (C2 anchor).</param>
    /// <param name="laneCount">The total number of fixed lanes.</param>
    /// <returns>
    /// A lane index in <c>[0, laneCount)</c>. The same non-null key always returns the same index
    /// within a process instance (fixed-lane affinity). A null key returns a round-robin index over
    /// the arrival sequence (keyless passthrough, parallel without ordering guarantees).
    /// </returns>
    internal static int ResolveLaneIndex(string? key, long arrivalSequence, int laneCount)
    {
        if (key is null)
        {
            // Keyless passthrough: round-robin over arrival sequence preserves the R8.5 behaviour for
            // messages that carry no orderable key. No ordering guarantee; parallel across lanes.
            return (int)((ulong)arrivalSequence % (ulong)laneCount);
        }

        // Fixed-lane: parse as Guid or derive a stable SHA-256-based Guid, then fold the hash code
        // into [0, laneCount). GetHashCode() on a Guid is stable within a process (sufficient for
        // LocalPartitioned single-instance semantics). The (uint) cast makes the modulo unsigned so
        // negative hash codes map correctly to a valid index.
        Guid keyGuid = GuidHelper.ParseOrHash(key);
        return (int)((uint)keyGuid.GetHashCode() % (uint)laneCount);
    }
}
