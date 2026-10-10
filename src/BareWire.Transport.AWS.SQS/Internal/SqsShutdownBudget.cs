namespace BareWire.Transport.AWS.SQS.Internal;

/// <summary>
/// One time budget shared by every broker call a stopping consumer makes while it hands back the messages
/// it still holds. The clock starts on the first <see cref="Token"/> access, so a consumer that never
/// has anything to hand back never starts it.
/// </summary>
internal sealed class SqsShutdownBudget(TimeSpan total) : IDisposable
{
    private CancellationTokenSource? _cts;

    /// <summary>Gets a token that is cancelled once the budget is spent.</summary>
    internal CancellationToken Token => (_cts ??= new CancellationTokenSource(total)).Token;

    /// <inheritdoc />
    public void Dispose() => _cts?.Dispose();
}
