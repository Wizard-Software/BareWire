using BareWire.Abstractions;

namespace BareWire.Core.FlowControl;

internal sealed class CreditManager : IDisposable
{
    private readonly FlowControlOptions _options;
    private readonly SemaphoreSlim _creditAvailable;
    private int _inflightCount;
    private long _inflightBytes;

    internal CreditManager(FlowControlOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _creditAvailable = new SemaphoreSlim(0, options.MaxInFlightMessages);
    }

    internal int InflightCount => Volatile.Read(ref _inflightCount);

    internal long InflightBytes => Volatile.Read(ref _inflightBytes);

    internal double UtilizationPercent => (double)InflightCount / _options.MaxInFlightMessages * 100;

    internal bool IsAtCapacity =>
        InflightCount >= _options.MaxInFlightMessages ||
        InflightBytes >= _options.MaxInFlightBytes;

    internal int TryGrantCredits(int requested)
    {
        if (requested <= 0)
            return 0;

        while (true)
        {
            int current = Volatile.Read(ref _inflightCount);
            int available = _options.MaxInFlightMessages - current;

            if (available <= 0)
                return 0;

            int grant = Math.Min(requested, available);
            int observed = Interlocked.CompareExchange(ref _inflightCount, current + grant, current);

            if (observed == current)
                return grant;

            // Another thread updated the count — retry.
        }
    }

    internal void TrackInflight(int count, long bytes)
    {
        Interlocked.Add(ref _inflightCount, count);
        Interlocked.Add(ref _inflightBytes, bytes);
    }

    // Tracks bytes for a message whose count slot was already reserved by TryGrantCredits.
    internal void TrackInflightBytes(long bytes)
    {
        Interlocked.Add(ref _inflightBytes, bytes);
    }

    // Asynchronously waits until a credit slot may be available.
    internal Task WaitForCreditAsync(CancellationToken cancellationToken)
    {
        return _creditAvailable.WaitAsync(cancellationToken);
    }

    internal void ReleaseInflight(int count, long bytes)
    {
        // Clamp to zero: release can never drive counts below zero even if the caller
        // releases more than it tracked (e.g. duplicate settle calls).
        while (true)
        {
            int current = Volatile.Read(ref _inflightCount);
            int next = Math.Max(0, current - count);
            if (Interlocked.CompareExchange(ref _inflightCount, next, current) == current)
                break;
        }

        while (true)
        {
            long currentBytes = Volatile.Read(ref _inflightBytes);
            long nextBytes = Math.Max(0L, currentBytes - bytes);
            if (Interlocked.CompareExchange(ref _inflightBytes, nextBytes, currentBytes) == currentBytes)
                break;
        }

        // Signal waiting consumers that a credit slot is available.
        // Guard against SemaphoreFullException: if the semaphore is already at max count,
        // no waiters need waking — swallow the exception rather than crashing the caller.
        try
        {
            _creditAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // Max permits reached — no waiters to wake.
        }
    }

    public void Dispose() => _creditAvailable.Dispose();
}
