using System.Collections.Concurrent;

namespace BareWire.Samples.ConsumerDefinitionShowcase.Services;

/// <summary>
/// The observation recorded once <see cref="Consumers.TransferConsumer"/> succeeds — i.e. after its
/// simulated transient failures have exhausted and the definition's retry policy has re-delivered the
/// message enough times. <see cref="Attempts"/> greater than 1 is the proof that retry fired.
/// </summary>
internal sealed record TransferObservation(
    string RunId,
    string RoutingKey,
    string ConsumerName,
    int Attempts,
    string TransferId);

/// <summary>
/// Thread-safe singleton sink combining a per-transfer attempt counter with the retry-proof
/// observation sink for the current POST /run invocation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NextAttempt"/> is a separate, longer-lived counter keyed by <c>TransferId</c> (a fresh
/// GUID per publish) rather than by run — it must survive across the retry policy's re-deliveries of
/// the same message, which all belong to the same run.
/// </para>
/// <para>
/// <see cref="WaitForAsync"/> uses the count-first-then-await pattern over a <see cref="SemaphoreSlim"/>
/// to avoid the lost-signal race — mirrors
/// <c>BareWire.Samples.ConsumerRoutingKeys.Services.RoutingObservations</c>: if the expected
/// observation is already recorded before <see cref="WaitForAsync"/> is entered, it returns
/// immediately instead of waiting on a signal that already fired.
/// </para>
/// </remarks>
internal sealed class TransferObservations : IDisposable
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    /// <summary>
    /// Atomically increments and returns the attempt count for <paramref name="transferId"/>, starting
    /// at 1. Called once per <c>ConsumeAsync</c> invocation, including retried re-deliveries of the
    /// same message.
    /// </summary>
    public int NextAttempt(string transferId)
        => _attempts.AddOrUpdate(transferId, addValue: 1, updateValueFactory: (_, current) => current + 1);

    /// <summary>
    /// Resets the observation state for the given run, discarding any prior observations.
    /// Must be called before publishing the scenario for this run.
    /// </summary>
    public void Reset(string runId)
    {
        if (_runs.TryRemove(runId, out RunState? old))
        {
            old.Dispose();
        }

        _runs[runId] = new RunState();
    }

    /// <summary>
    /// Records the retry-proof observation. Called from <see cref="Consumers.TransferConsumer.ConsumeAsync"/>
    /// on the transport thread pool once the consumer has succeeded.
    /// </summary>
    public void Record(TransferObservation observation)
    {
        if (_runs.TryGetValue(observation.RunId, out RunState? state))
        {
            state.Queue.Enqueue(observation);

            // Release one token; releases accumulate so no signal is ever lost if the consumer
            // records before WaitForAsync begins awaiting.
            state.Signal.Release();
        }
    }

    /// <summary>
    /// Waits until <paramref name="expected"/> observations have been recorded for
    /// <paramref name="runId"/>, or until <paramref name="timeout"/> elapses. Returns whatever
    /// observations were recorded (may be fewer than expected on timeout).
    /// </summary>
    public async Task<IReadOnlyList<TransferObservation>> WaitForAsync(
        string runId,
        int expected,
        TimeSpan timeout)
    {
        if (!_runs.TryGetValue(runId, out RunState? state))
        {
            return [];
        }

        using var cts = new CancellationTokenSource(timeout);

        while (true)
        {
            // Count-first: return immediately if all expected records are already present.
            if (state.Queue.Count >= expected)
            {
                return [.. state.Queue];
            }

            try
            {
                await state.Signal.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timeout — return whatever was recorded.
                return [.. state.Queue];
            }
        }
    }

    public void Dispose()
    {
        foreach (RunState state in _runs.Values)
        {
            state.Dispose();
        }

        _runs.Clear();
    }

    private sealed class RunState : IDisposable
    {
        public ConcurrentQueue<TransferObservation> Queue { get; } = new();

        // Initial count 0, no upper bound — Release() calls accumulate tokens so WaitAsync never
        // misses a signal that fired before it started.
        public SemaphoreSlim Signal { get; } = new(0, int.MaxValue);

        public void Dispose() => Signal.Dispose();
    }
}
