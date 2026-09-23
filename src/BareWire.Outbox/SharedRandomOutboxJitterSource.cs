namespace BareWire.Outbox;

// Default jitter source. Stateless — Random.Shared is thread-safe — so a single immutable
// instance is shared by every store and by the DI registration.
internal sealed class SharedRandomOutboxJitterSource : IOutboxJitterSource
{
    internal static readonly SharedRandomOutboxJitterSource Instance = new();

    public double NextDouble() => Random.Shared.NextDouble();
}
