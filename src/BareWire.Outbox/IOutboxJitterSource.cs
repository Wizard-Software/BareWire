namespace BareWire.Outbox;

// Injectable randomness source for outbox retry jitter. The default implementation delegates to
// Random.Shared; tests substitute a deterministic source so retry scheduling stays reproducible.
internal interface IOutboxJitterSource
{
    // Returns a uniformly distributed value in the half-open interval [0.0, 1.0).
    double NextDouble();
}
