namespace BareWire.Bus;

/// <summary>
/// Computes the per-lane channel depth for the ordered dispatch stage
/// (<see cref="ReceiveEndpointRunner"/> ordering-ON path, ADR-026 §7 — P2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two orthogonal bounded axes (ADR-026 §7, P2):</strong>
/// The ordered consume path is bounded by two independent axes that together prevent unbounded
/// memory growth while preserving liveness.
/// <list type="bullet">
/// <item>
/// <term>Axis 1 — global inflight credit</term>
/// <description>
/// <c>MaxInFlightMessages</c> (seeded from <c>PrefetchCount</c>) is the total count of
/// messages that may be simultaneously in-flight across ALL lanes. Credit is granted by the
/// <c>CreditManager</c> before each message enters the pipeline and released by each lane
/// worker when processing finishes. This axis is the global budget.
/// </description>
/// </item>
/// <item>
/// <term>Axis 2 — per-lane depth (this)</term>
/// <description>
/// Each lane's <see cref="System.Threading.Channels.Channel{T}"/> is bounded to a fixed
/// depth. The depth limits how many messages any single lane may hold concurrently, so one
/// hot partition cannot grow without bound. This axis is local to each lane.
/// </description>
/// </item>
/// </list>
/// The two axes are <em>orthogonal</em>: a global credit wait (axis 1) does not stall lane
/// workers, and a full-lane wait (axis 2) does not consume credits from other lanes.
/// </para>
/// <para>
/// <strong>No-deadlock invariant (plan §3.3):</strong>
/// Lane workers drain independently of the single reader — each worker runs on its own
/// <c>Task.Run</c> loop and reads from its channel with <c>CancellationToken.None</c>,
/// so a reader blocked on axis 1 (credit wait) or axis 2 (full-lane backpressure) does NOT
/// prevent workers from making progress. As long as each lane's depth is at least 1, the
/// head of every lane is always admittable; at least one lane worker always has work to drain,
/// which releases credits (axis 1) and channel capacity (axis 2), unblocking the reader.
/// A depth of 0 is therefore illegal — <see cref="Resolve"/> always returns at least 1.
/// </para>
/// <para>
/// <strong>Message-count bound only — NOT byte-bound:</strong>
/// The depth computed here limits the <em>count</em> of messages per lane.
/// <c>MaxInFlightBytes</c> does not gate intake in R8.7 (ADR-026 §7 explicitly prohibits
/// claiming byte-bounded buffering until <c>MaxInFlightBytes</c> is wired into the intake
/// path). No byte-bound claim is made here or in callers of this method.
/// </para>
/// </remarks>
internal static class OrderedDispatchLaneDepth
{
    /// <summary>
    /// Resolves the per-lane channel depth for the ordered dispatch stage.
    /// </summary>
    /// <param name="laneCount">
    /// The number of lanes in the ordered dispatch stage (must be positive; values &lt;= 0 are
    /// clamped to 1 before use).
    /// </param>
    /// <param name="maxInFlightMessages">
    /// The global inflight message budget (axis 1 — typically <c>PrefetchCount</c>). Values
    /// &lt;= 0 are clamped to 1 before use.
    /// </param>
    /// <param name="configuredDepth">
    /// An explicit per-lane depth override, or <see langword="null"/> to use the default
    /// budget-proportional policy. This parameter is a forward-looking hook for a possible
    /// future public lane-depth knob; it is deliberately always <see langword="null"/> in R8.7.
    /// When supplied, the value is still clamped to at least 1 to preserve the no-deadlock
    /// invariant.
    /// </param>
    /// <returns>
    /// The per-lane channel capacity (always &gt;= 1). The default policy distributes the
    /// global budget evenly across lanes using ceiling division:
    /// <c>ceil(maxInFlightMessages / laneCount)</c>. The minimum of 1 is always enforced to
    /// guarantee the no-deadlock invariant.
    /// </returns>
    internal static int Resolve(int laneCount, int maxInFlightMessages, int? configuredDepth)
    {
        // Forward-looking explicit override — always null in R8.7.
        if (configuredDepth.HasValue)
        {
            return Math.Max(1, configuredDepth.Value);
        }

        // Clamp inputs to avoid division by zero and degenerate budgets.
        int clampedLanes = Math.Max(1, laneCount);
        int clampedBudget = Math.Max(1, maxInFlightMessages);

        // Integer ceiling division: ceil(budget / lanes).
        int depth = (clampedBudget + clampedLanes - 1) / clampedLanes;

        // No-deadlock invariant: channel capacity MUST be >= 1.
        return Math.Max(1, depth);
    }
}
