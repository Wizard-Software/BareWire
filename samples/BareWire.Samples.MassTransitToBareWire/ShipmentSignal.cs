namespace BareWire.Samples.MassTransitToBareWire;

/// <summary>
/// Deterministic completion signal for the raw fire-and-forget round.
///
/// <para>
/// <c>IBus.PublishAsync&lt;ShipmentNotice&gt;</c> returns once the message has reached the broker,
/// NOT once the consumer has processed it. Without a settle point, the host could shut down
/// before <c>ShipmentConsumer</c> publishes its <c>ShipmentRecorded</c> event, making the
/// smoke test flaky. The driver awaits <see cref="Recorded"/> (bounded by a timeout) so the
/// async raw round always completes before graceful shutdown.
/// </para>
/// </summary>
internal sealed class ShipmentSignal
{
    private readonly TaskCompletionSource _recorded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when <c>ShipmentConsumer</c> has published its <c>ShipmentRecorded</c> event.</summary>
    public Task Recorded => _recorded.Task;

    /// <summary>Signals that the raw round finished. Idempotent — extra calls are ignored.</summary>
    public void MarkRecorded() => _recorded.TrySetResult();
}
