namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// The part of a consumer that settlement needs while the consumer is stopping: a way to learn that receiving
/// is being shut down and a way to wait until no receive call is in flight any more.
/// </summary>
internal interface IAzureServiceBusReceiveControl
{
    /// <summary>Gets a value indicating whether receiving has been asked to stop.</summary>
    bool IsStopRequested { get; }

    /// <summary>
    /// Stops receiving and completes once no receive call is in flight. Single-flight: every caller gets the
    /// same task, which never faults.
    /// </summary>
    Task EnsureReceiveStoppedAsync();
}
