namespace BareWire.Outbox.EntityFramework.Internal;

// Turn of a contested single-slot claim, shared by every EfCoreOutboxStore resolved from one
// service provider (registered as a singleton), so the two row classes alternate deterministically
// across dispatch cycles even though each cycle gets a fresh store instance.
internal sealed class OutboxSingleSlotTurn
{
    private int _consultations;

    // First call returns false (new rows go first), then true, false, ... Thread-safe.
    internal bool NextIsRetryTurn() => (Interlocked.Increment(ref _consultations) & 1) == 0;
}
