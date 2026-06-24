using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
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
/// <strong>SEC discipline (S1/S2 — ADR-026 §NIE WOLNO):</strong> Key values are never logged,
/// thrown, or included in diagnostic strings. The key is hashed immediately in
/// <see cref="ResolveLaneIndex"/> and the raw string value does not survive past that point.
/// The only sanctioned diagnostic representation of a key is the opaque correlation token from
/// <see cref="OrderingKeyDiagnostics.ToOpaqueToken"/> (correlation-only, not a cryptographic
/// anonymizer); fail-fast ordering-configuration errors are built via
/// <see cref="OrderingKeyDiagnostics.OrderingConfigError"/>, which forces
/// <c>OptionValue</c> to <see langword="null"/>. No per-key metric dimension is emitted.
/// Enforced by the <c>OrderingSecurityTests</c> contract suite.
/// </para>
/// <para>
/// <strong>Typed selector — delivered by <see cref="ResolveTyped"/> (R8.13 seam):</strong> The hot
/// fan-out path (<c>ReceiveEndpointRunner.EnqueueAsync</c>) runs on the raw
/// <see cref="BareWire.Abstractions.Transport.InboundMessage"/> BEFORE deserialization and calls
/// <see cref="Resolve"/> (header / correlation-id only — both readable from raw headers without
/// deserialization, preserving ADR-003 zero-copy). The typed selector reads a CLR property that
/// only exists AFTER deserialization, so it is resolved by <see cref="ResolveTyped"/>, a documented
/// seam with <strong>no runtime caller until R8.15+</strong> (R8.6 keeps the fan-out before
/// deserialization). Until then, an endpoint configured with only <c>By&lt;TMessage&gt;(selector)</c>
/// (no header) is treated by <see cref="Resolve"/> as keyless on the fan-out path.
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

        // (b) Typed selector — resolved by ResolveTyped (R8.13), NOT here.
        // This method runs on the raw InboundMessage BEFORE deserialization, where the CLR property the
        // selector reads does not yet exist. Resolving it here would force premature deserialization,
        // violating ADR-003 (zero-copy). When only a selector is configured (no HeaderName), the fan-out
        // path treats the message as keyless; the typed selector is delivered by the ResolveTyped seam,
        // which has no runtime caller until R8.15+ (R8.6 keeps fan-out before deserialization).

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
    /// Resolves the ordering key over the full key-source chain INCLUDING the typed selector (R8.13, M3):
    /// explicit header → typed selector (over the deserialized message) → correlation-id → keyless.
    /// </summary>
    /// <param name="ordering">The ordering configuration for the endpoint.</param>
    /// <param name="selectorAdapter">
    /// The configuration-time adapter (<c>o =&gt; selector((TMessage)o)</c>) built by
    /// <see cref="Configuration.ConsumerOrderingConfiguration.By{TMessage}"/> (D2 — no
    /// <c>DynamicInvoke</c>, <c>object[]</c>, or reflection). <see langword="null"/> when no selector is
    /// configured. Passed explicitly because the public <see cref="IConsumerOrderingConfiguration"/> view
    /// does not expose the adapter (D2 — public API unchanged); the caller reads it from the concrete
    /// internal carrier.
    /// </param>
    /// <param name="deserializedMessage">
    /// The deserialized message instance, or <see langword="null"/> when no deserialized object is
    /// available (the selector member then falls through to the correlation-id / keyless tail).
    /// </param>
    /// <param name="headers">The message headers.</param>
    /// <returns>
    /// The resolved key string, or <see langword="null"/> for keyless messages (round-robin lane
    /// assignment — no ordering guarantee).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Seam — no runtime caller until R8.15+ (D1).</strong> R8.6 hashes lanes on the raw
    /// <see cref="BareWire.Abstractions.Transport.InboundMessage"/> BEFORE deserialization, so there is no
    /// post-deserialization execution point to call this method on the dispatch path yet. R8.13 delivers
    /// the method and its unit tests; runtime wiring (a post-deserialization caller that supplies the
    /// deserialized message) is R8.15+.
    /// </para>
    /// <para>
    /// <strong>Heterogeneous streams.</strong> When the configured selector's message type
    /// (<see cref="IConsumerOrderingConfiguration.SelectorMessageType"/>) does not match
    /// <paramref name="deserializedMessage"/>, or the selector returns <see langword="null"/>, the message
    /// is keyless — never an exception (documented allowed behaviour).
    /// </para>
    /// <para>
    /// <strong>SEC (S1 — ADR-026 §NIE WOLNO, D3).</strong> The user selector and the stringification of its
    /// result are the only points where caller code runs; both are wrapped in a single guard. Any throw is
    /// re-thrown as a key-free <see cref="BareWireException"/> whose message is composed exclusively from the
    /// constant <see cref="OrderingKeyDiagnostics.SelectorPlaceholder"/> and the message type NAME (safe —
    /// configuration, not data). The original exception is NOT attached as <c>InnerException</c> — its
    /// <c>Message</c> / <c>StackTrace</c> could carry the projected key value or message payload.
    /// </para>
    /// </remarks>
    internal static string? ResolveTyped(
        IConsumerOrderingConfiguration ordering,
        Func<object, object?>? selectorAdapter,
        object? deserializedMessage,
        IReadOnlyDictionary<string, string> headers)
    {
        // (a) Header source — explicit header name takes highest precedence (cross-instance-safe, symmetric
        // to the producer side). Identical to Resolve's (a) — header wins over a configured selector.
        if (ordering.HeaderName is not null)
        {
            headers.TryGetValue(ordering.HeaderName, out string? headerValue);
            return headerValue; // may be null if header absent → keyless
        }

        // (b) Typed selector — the active R8.13 member. Requires both an adapter and a deserialized message.
        if (selectorAdapter is not null && deserializedMessage is not null)
        {
            // Heterogeneous stream: if the deserialized message is not an instance of the selector's
            // message type, treat it as keyless rather than invoking the adapter (which would throw
            // InvalidCastException on the (TMessage) cast). This is documented, allowed behaviour.
            if (ordering.SelectorMessageType is { } selectorType
                && !selectorType.IsInstanceOfType(deserializedMessage))
            {
                return null; // keyless — selector does not apply to this message type
            }

            // SEC GUARD (D3 + V1): both the selector invocation AND value.ToString() run under one
            // try/catch. A user selector — or a custom ToString() on its result — may throw an exception
            // whose Message/StackTrace carries the projected key value or message payload (PII). That throw
            // MUST NOT escape as-is: re-throw a key-free BareWireException with NO inner exception.
            string? projectedKey;
            try
            {
                object? projected = selectorAdapter(deserializedMessage);
                projectedKey = projected?.ToString(); // V1: ToString() is INSIDE the guard (may throw PII)
            }
            catch (Exception ex)
            {
                // Defensive unwrap of TargetInvocationException (the adapter uses no reflection, but a
                // future caller might). We intentionally do NOT pass the original/unwrapped exception as
                // innerException — its Message/StackTrace are a PII vector (S1). The key-free message is
                // built only from the constant selector placeholder and the message type NAME (safe config).
                _ = ex is System.Reflection.TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
                throw new BareWireException(
                    $"Ordering key selector {OrderingKeyDiagnostics.SelectorPlaceholder} threw while projecting "
                    + $"the ordering key for message type '{ordering.SelectorMessageType?.Name ?? "<unknown>"}'. "
                    + "The selector must not throw; the projected value and message payload are intentionally omitted.");
            }

            return projectedKey; // may be null → keyless (heterogeneous stream)
        }

        // (c) Correlation-id source. M3: limited to LocalPartitioned semantics — this method returns the
        // correlation-id value only as a LOCAL key candidate; it never silently promotes it to a transport
        // routing key. Strategy-level enforcement (no correlation-id fallback under TransportNative
        // cross-instance) lives in the strategy resolver (R8.11).
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
