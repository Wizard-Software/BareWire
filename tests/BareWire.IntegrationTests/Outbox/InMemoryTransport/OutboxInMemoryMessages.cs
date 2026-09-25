using System.Collections.Concurrent;
using BareWire.Abstractions;

namespace BareWire.IntegrationTests.Outbox.InMemoryTransport;

// ── Scenario 1 messages ──────────────────────────────────────────────────────────────────────────

/// <summary>A row routed to the permanently latched queue — negative <see cref="N"/> marks a latch filler, never seeded through the outbox.</summary>
public sealed record StuckRow(int N);

/// <summary>A row routed to the healthy queue whose drain-without-starvation behavior scenario 1 proves.</summary>
public sealed record FreshRow(int N);

// ── Shared test probe ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-test singleton injected into every consumer below — no static mutable state. Gates the consumer
/// bound to the permanently stalled queue and records how many times each (queue, payload id) pair was
/// handled, so a test can assert exactly-once delivery per queue.
/// </summary>
internal sealed class GateProbe
{
    /// <summary>Released once, at the end of the test, to let the gated consumer(s) finish processing their in-flight message.</summary>
    internal TaskCompletionSource StuckGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Number of times each (queue name, payload id) pair was handled by its consumer.</summary>
    internal ConcurrentDictionary<(string Queue, int N), int> Handled { get; } = new();
}

// ── Scenario 1 consumers ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Bound to the permanently latched queue. Waits on <see cref="GateProbe.StuckGate"/> as its FIRST
/// instruction — before any database operation — so the consumer never holds a SQLite write lock while
/// blocked (see <c>OutboxInMemoryHost</c> remarks on <c>TransactionalOutboxMiddleware</c> timing).
/// </summary>
internal sealed class StuckRowConsumer(GateProbe probe) : IConsumer<StuckRow>
{
    public async Task ConsumeAsync(ConsumeContext<StuckRow> context)
    {
        await probe.StuckGate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        probe.Handled.AddOrUpdate(("s1-stuck", context.Message.N), 1, static (_, count) => count + 1);
    }
}

/// <summary>Bound to the healthy queue — records every delivery so the test can assert exactly-once handling.</summary>
internal sealed class FreshRowConsumer(GateProbe probe) : IConsumer<FreshRow>
{
    public Task ConsumeAsync(ConsumeContext<FreshRow> context)
    {
        probe.Handled.AddOrUpdate(("s1-healthy", context.Message.N), 1, static (_, count) => count + 1);
        return Task.CompletedTask;
    }
}

// ── Scenario 2 messages ──────────────────────────────────────────────────────────────────────────

/// <summary>A row fanned out to two healthy queues and one permanently latched queue — negative <see cref="N"/> marks a latch filler, never seeded through the outbox.</summary>
public sealed record OrderPlaced(int N);

// ── Scenario 2 consumers ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Bound to the first healthy fan-out queue. The production Inbox filters out duplicate deliveries of a
/// retried row before this consumer ever runs, so each row id is expected to be handled exactly once
/// despite the row having been re-sent multiple times while the third subscriber was latched.
/// </summary>
internal sealed class SubAConsumer(GateProbe probe) : IConsumer<OrderPlaced>
{
    public Task ConsumeAsync(ConsumeContext<OrderPlaced> context)
    {
        probe.Handled.AddOrUpdate(("s2-sub-a", context.Message.N), 1, static (_, count) => count + 1);
        return Task.CompletedTask;
    }
}

/// <summary>Bound to the second healthy fan-out queue — see <see cref="SubAConsumer"/> remarks.</summary>
internal sealed class SubBConsumer(GateProbe probe) : IConsumer<OrderPlaced>
{
    public Task ConsumeAsync(ConsumeContext<OrderPlaced> context)
    {
        probe.Handled.AddOrUpdate(("s2-sub-b", context.Message.N), 1, static (_, count) => count + 1);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bound to the permanently latched fan-out queue. Waits on <see cref="GateProbe.StuckGate"/> as its
/// FIRST instruction — before any database operation — exactly like <see cref="StuckRowConsumer"/>.
/// </summary>
internal sealed class StuckOrderConsumer(GateProbe probe) : IConsumer<OrderPlaced>
{
    public async Task ConsumeAsync(ConsumeContext<OrderPlaced> context)
    {
        await probe.StuckGate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        probe.Handled.AddOrUpdate(("s2-stuck", context.Message.N), 1, static (_, count) => count + 1);
    }
}

// ── Scenario 3 messages ──────────────────────────────────────────────────────────────────────────

/// <summary>The head-of-key row routed to the permanently latched queue — negative <see cref="N"/> marks a latch filler.</summary>
public sealed record HeadStep(int N);

/// <summary>A row sharing the head's ordering key (or unkeyed when <see cref="N"/> is used standalone), routed to the healthy ordered queue.</summary>
public sealed record KeyedStep(int N);

// ── Scenario 3 consumers ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Bound to the permanently latched head-of-key queue. Waits on <see cref="GateProbe.StuckGate"/> as its
/// FIRST instruction, exactly like <see cref="StuckRowConsumer"/>.
/// </summary>
internal sealed class HeadStepConsumer(GateProbe probe) : IConsumer<HeadStep>
{
    public async Task ConsumeAsync(ConsumeContext<HeadStep> context)
    {
        await probe.StuckGate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        probe.Handled.AddOrUpdate(("s3-stuck", context.Message.N), 1, static (_, count) => count + 1);
    }
}

/// <summary>Bound to the healthy ordered queue — records every delivery so the test can assert exactly-once handling despite barrier-released duplicates.</summary>
internal sealed class KeyedStepConsumer(GateProbe probe) : IConsumer<KeyedStep>
{
    public Task ConsumeAsync(ConsumeContext<KeyedStep> context)
    {
        probe.Handled.AddOrUpdate(("s3-ordered", context.Message.N), 1, static (_, count) => count + 1);
        return Task.CompletedTask;
    }
}
