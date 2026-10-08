namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// Decides, once per application, whether the transactional outbox middleware uses an ambient
/// <c>TransactionScope</c> or an explicit local transaction. The provider name is read lazily and
/// the result memoized, so the per-message hot path pays no repeated provider lookup.
/// </summary>
internal sealed class OutboxTransactionMode
{
    private const int Unresolved = 0;
    private const int Ambient = 1;
    private const int Local = 2;

    private readonly Func<string?>? _providerNameFactory;
    private int _state;

    /// <summary>Creates a mode resolved lazily from the provider name returned by <paramref name="providerNameFactory"/>.</summary>
    internal OutboxTransactionMode(Func<string?> providerNameFactory)
    {
        ArgumentNullException.ThrowIfNull(providerNameFactory);
        _providerNameFactory = providerNameFactory;
    }

    /// <summary>Creates a mode with a forced value, bypassing provider detection (test seam).</summary>
    internal OutboxTransactionMode(bool useAmbientTransaction)
    {
        _state = useAmbientTransaction ? Ambient : Local;
    }

    /// <summary>
    /// Gets whether an ambient <c>TransactionScope</c> should be used; <see langword="false"/> means
    /// an explicit local transaction. Thread-safe; the provider name is read at most once.
    /// </summary>
    internal bool UseAmbientTransaction
    {
        get
        {
            int state = Volatile.Read(ref _state);
            if (state == Unresolved)
            {
                int resolved = AmbientTransactionSupport.IsSupported(_providerNameFactory!()) ? Ambient : Local;
                // Concurrent resolvers compute the same value; the first writer wins.
                state = Interlocked.CompareExchange(ref _state, resolved, Unresolved);
                if (state == Unresolved)
                {
                    state = resolved;
                }
            }

            return state == Ambient;
        }
    }
}
