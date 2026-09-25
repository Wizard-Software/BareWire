namespace BareWire.Bus;

/// <summary>
/// Configures how long <see cref="BareWireBusControl.StopAsync"/> waits for in-flight work to
/// settle (drain) before cancelling consumer loops, for transport adapters that opt into the
/// internal graceful-drain coordination protocol.
/// </summary>
internal sealed class BusShutdownOptions
{
    /// <summary>The drain limit applied when no <see cref="BusShutdownOptions"/> is registered.</summary>
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(10);

    // The largest delay the underlying timer (used by CancellationTokenSource.CancelAfter and
    // Task.Delay) accepts is uint.MaxValue - 1 milliseconds (about 49.7 days). A caller-supplied
    // value larger than that would pass a simple "greater than zero" check yet throw
    // ArgumentOutOfRangeException once the drain timer is armed mid-shutdown, so the upper bound
    // is enforced here instead.
    private static readonly TimeSpan MaxDrainTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Gets the maximum time <see cref="BareWireBusControl.StopAsync"/> waits for quiescence
    /// before cancelling consumer loops, for a transport adapter that implements the internal
    /// graceful-drain coordination protocol. Defaults to 10 seconds.
    /// </summary>
    /// <remarks>
    /// Message publishing that happens outside of a consumer handler — for example a hosted
    /// service publishing on its own schedule while the bus is stopping — keeps the bus busy and
    /// can extend the drain up to this limit before consumer loops are cancelled.
    /// </remarks>
    public TimeSpan DrainTimeout
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxDrainTimeout);
            field = value;
        }
    } = DefaultDrainTimeout;
}
