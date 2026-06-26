using System.Collections.Concurrent;

namespace BareWire.Samples.ConsumerRoutingKeys.Services;

/// <summary>
/// A single consumer dispatch observation recorded by a consumer's ConsumeAsync.
/// </summary>
internal sealed record RoutingObservation(
    string RunId,
    string RoutingKey,
    string ConsumerName,
    string MessageType,
    bool TypeLess,
    string Echo);

/// <summary>
/// Thread-safe singleton sink for consumer dispatch observations.
/// Each call to POST /run resets its run state and waits for a bounded number of
/// observations before returning them to the caller.
/// </summary>
/// <remarks>
/// WaitForAsync uses the count-first-then-await pattern over a SemaphoreSlim to avoid
/// the lost-signal race: if all expected consumers record their observations before
/// WaitForAsync is entered, the accumulated semaphore releases allow immediate return
/// without waiting. This avoids the scenario where signals are emitted before the waiter
/// subscribes and are never received.
/// </remarks>
internal sealed class RoutingObservations : IDisposable
{
    private readonly ConcurrentDictionary<string, RunState> _runs =
        new(StringComparer.Ordinal);

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
    /// Records one consumer dispatch observation.
    /// Called from consumer ConsumeAsync on the transport thread pool.
    /// </summary>
    public void Record(
        string runId,
        string routingKey,
        string consumerName,
        string messageType,
        bool typeLess,
        string echo)
    {
        if (_runs.TryGetValue(runId, out RunState? state))
        {
            state.Queue.Enqueue(
                new RoutingObservation(runId, routingKey, consumerName, messageType, typeLess, echo));

            // Release one token; releases accumulate so no signal is ever lost if the
            // consumer records before WaitForAsync begins awaiting.
            state.Signal.Release();
        }
    }

    /// <summary>
    /// Waits until <paramref name="expected"/> observations have been recorded for
    /// <paramref name="runId"/>, or until <paramref name="timeout"/> elapses.
    /// Returns whatever observations were recorded (may be fewer than expected on timeout).
    /// </summary>
    public async Task<IReadOnlyList<RoutingObservation>> WaitForAsync(
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
            // This handles the race where all consumers finish before this method is entered.
            if (state.Queue.Count >= expected)
            {
                return [.. state.Queue];
            }

            try
            {
                // Wait for the next Record() call. Accumulated releases mean we never block
                // on signals that already fired.
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
        public ConcurrentQueue<RoutingObservation> Queue { get; } = new();

        // Initial count 0, no upper bound — Release() calls accumulate tokens so
        // WaitAsync never misses a signal that fired before it started.
        public SemaphoreSlim Signal { get; } = new(0, int.MaxValue);

        public void Dispose() => Signal.Dispose();
    }
}
