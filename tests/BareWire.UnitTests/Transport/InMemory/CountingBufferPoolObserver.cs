using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BareWire.Transport.InMemory.Internal;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Test hook implementing <see cref="IInMemoryBufferPoolObserver"/>: tracks every buffer rented through
/// an <see cref="InMemoryBufferPool"/> by reference identity, so a test can assert that every rent this
/// transport performs on its own behalf is returned exactly once, and never more than once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope: hook-rented buffers only.</b> The send path (<c>InMemorySender.Commit</c>) rents every buffer
/// it needs through the owning adapter's <see cref="InMemoryBufferPool"/>, so this observer sees it —
/// unless a test enqueues a seed delivery directly via <c>ArrayPool&lt;byte&gt;.Shared.Rent</c>, bypassing
/// the transport entirely, which never registers that buffer here. A buffer handed to an
/// <see cref="BareWire.Abstractions.Transport.InboundMessage"/> — whether it came from the send path, a
/// settlement copy, or a redelivery — is returned by that message's own <c>Dispose()</c> directly to
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/>, never through <see cref="InMemoryBufferPool"/>.
/// <see cref="MarkReturnedByMessage"/> exists to close the loop for that case (a hook-rented buffer that
/// ends up owned by a message and is returned on the message's own <c>Dispose()</c>); a buffer this
/// observer never saw rented (a directly seeded one) is tolerated as a no-op rather than flagged — this
/// observer has no way to prove that call wrong, so it only flags what it CAN prove wrong: a buffer this
/// observer knows was already closed out (returned, or marked returned-by-message) being marked again
/// with no rent in between.
/// </para>
/// <para>
/// <b>Reference reuse.</b> <see cref="System.Buffers.ArrayPool{T}.Shared"/> may legitimately hand out the
/// SAME physical array for two unrelated logical rents once the first is returned — this observer tracks
/// per-buffer STATE (outstanding vs. returned), not a permanent "ever seen" set, so a buffer's second
/// rent-then-return cycle is judged independently of its first and is never mistaken for a double return.
/// </para>
/// </remarks>
internal sealed class CountingBufferPoolObserver : IInMemoryBufferPoolObserver
{
    private readonly ConcurrentDictionary<byte[], RentState> _state = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<string> _violations = new();
    private int _rented;
    private int _returned;

    private enum RentState
    {
        Outstanding,
        Returned,
    }

    /// <summary>Gets the total number of rents observed (across every rent-return cycle, including reused buffers).</summary>
    internal int Rented => Volatile.Read(ref _rented);

    /// <summary>Gets the total number of returns observed (through the pool or via <see cref="MarkReturnedByMessage"/>).</summary>
    internal int Returned => Volatile.Read(ref _returned);

    /// <summary>Gets the number of hook-rented buffers currently outstanding (rented, not yet returned by any means).</summary>
    internal int Outstanding => _state.Count(static kvp => kvp.Value == RentState.Outstanding);

    /// <summary>Gets every violation recorded so far.</summary>
    internal IReadOnlyList<string> Violations => [.. _violations];

    /// <summary>Gets whether <paramref name="buffer"/> was rented through the hook and is not yet returned.</summary>
    internal bool IsOutstanding(byte[] buffer) => _state.TryGetValue(buffer, out RentState state) && state == RentState.Outstanding;

    /// <inheritdoc />
    public void OnRented(byte[] buffer)
    {
        Interlocked.Increment(ref _rented);
        if (_state.TryGetValue(buffer, out RentState existing) && existing == RentState.Outstanding)
        {
            _violations.Enqueue("A buffer already outstanding was rented again (same reference).");
        }

        _state[buffer] = RentState.Outstanding;
    }

    /// <inheritdoc />
    public void OnReturned(byte[] buffer)
    {
        Interlocked.Increment(ref _returned);
        if (_state.TryGetValue(buffer, out RentState existing) && existing == RentState.Outstanding)
        {
            _state[buffer] = RentState.Returned;
        }
        else
        {
            _violations.Enqueue("A buffer not currently outstanding (never rented, or already returned) was returned.");
        }
    }

    /// <summary>
    /// Marks <paramref name="buffer"/> — a buffer this observer saw rented and that ended up owned by an
    /// <see cref="BareWire.Abstractions.Transport.InboundMessage"/> — as returned, after the test has
    /// asserted the message's own <c>Dispose()</c> already returned it to the shared pool (i.e. after
    /// asserting <c>message.PooledBuffer is null</c>). Closes the ownership chain for
    /// <see cref="Outstanding"/> without this observer ever intercepting the message's own return.
    /// </summary>
    /// <remarks>
    /// A buffer this observer never saw rented is tolerated silently (see this type's remarks on scope).
    /// A buffer this observer knows is already <see cref="RentState.Returned"/> — with no intervening
    /// <see cref="OnRented"/> call — being marked again IS flagged: that can only be a double-count by the
    /// caller, never a legitimate reused buffer (a legitimate reuse always goes through <see cref="OnRented"/>
    /// first, which resets the state back to <see cref="RentState.Outstanding"/>).
    /// </remarks>
    /// <param name="buffer">The buffer a message just returned to the shared pool via its own <c>Dispose()</c>.</param>
    internal void MarkReturnedByMessage(byte[] buffer)
    {
        if (!_state.TryGetValue(buffer, out RentState existing))
        {
            // Never rented through the hook — tolerated; see this type's remarks.
            return;
        }

        if (existing == RentState.Outstanding)
        {
            _state[buffer] = RentState.Returned;
            Interlocked.Increment(ref _returned);
            return;
        }

        _violations.Enqueue(
            "A buffer already marked returned (with no intervening rent) was marked returned-by-message again.");
    }
}
