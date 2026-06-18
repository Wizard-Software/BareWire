namespace BareWire.Saga.Redis;

/// <summary>
/// Configuration options for <see cref="RedisSagaRepository{TSaga}"/>.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY NOTE (SEC-1): SAGA state is stored as unencrypted plaintext JSON in Redis.
/// At-rest confidentiality (TLS in transit, Redis-level or value-level encryption)
/// is the responsibility of the Redis connection configuration (R6.2). Do not store
/// secrets or PII in SAGA state unless the Redis deployment is appropriately secured.
/// </para>
/// <para>
/// SECURITY NOTE (SEC-2): <see cref="KeyPrefix"/> is a trusted developer-controlled value.
/// Do not populate it from end-user input. The default value is derived from the saga type name.
/// </para>
/// </remarks>
public sealed class RedisSagaRepositoryOptions
{
    /// <summary>
    /// Gets or sets the Redis key prefix used to namespace SAGA state entries.
    /// The full key format is <c>{KeyPrefix}:{CorrelationId:D}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to the name of the SAGA type (e.g., <c>"OrderSaga"</c>) to prevent key collisions
    /// between different SAGA types. This is a trusted developer-controlled value — do not
    /// populate it from end-user input. Avoid characters that have special meaning in Redis
    /// key patterns such as <c>:</c>, <c>{</c>, <c>}</c>, or whitespace, as they may interfere
    /// with cluster hash tags or key scanning.
    /// </para>
    /// </remarks>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional time-to-live applied to each SAGA state entry in Redis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="null"/> (the default), SAGA state entries do not expire and persist
    /// indefinitely in Redis until explicitly deleted via <c>DeleteAsync</c>.
    /// </para>
    /// <para>
    /// WARNING: Setting a non-null TTL introduces a risk that Redis may evict a live SAGA's
    /// state before the SAGA reaches a terminal state. This can cause <c>FindAsync</c> to
    /// return <see langword="null"/> for a SAGA that is logically still active, leading to
    /// unrecoverable state loss. Use this option only when SAGA lifetimes are well-bounded
    /// and shorter than the configured TTL.
    /// </para>
    /// </remarks>
    public TimeSpan? StateTtl { get; set; }
}
