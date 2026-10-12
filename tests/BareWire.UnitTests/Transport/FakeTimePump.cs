using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport;

/// <summary>
/// Drives a <see cref="FakeTimeProvider"/> for tests that wait on a time budget: fake time is advanced in
/// steps until the awaited task completes, so the order in which the code under test creates its timers
/// relative to the advance does not matter.
/// </summary>
internal static class FakeTimePump
{
    /// <summary>
    /// Advances fake time by <paramref name="step"/> until <paramref name="task"/> completes; a real 1 ms
    /// yield between steps lets timers and continuations run. A timer created after an advance is fired by
    /// the next one. Throws <see cref="TimeoutException"/> once <paramref name="guard"/> (real time) elapses.
    /// </summary>
    internal static async Task AdvanceUntilCompletedAsync(
        FakeTimeProvider fake, Task task, TimeSpan step, TimeSpan guard)
    {
        long deadline = Environment.TickCount64 + (long)guard.TotalMilliseconds;
        while (!task.IsCompleted)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Fake-time pump guard elapsed.");
            }

            fake.Advance(step);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await task;
    }
}
