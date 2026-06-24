using Microsoft.Extensions.Logging;

namespace BareWire.Bus;

/// <summary>
/// Consume-side re-map detection for the consistent-hash lane topology (R8.12 C4, ADR-026 §2.1).
/// Tracks the last observed <c>BW-MappingEpoch</c> per lane and emits a Warning when the epoch
/// changes — signalling that the consistent-hash key space was re-mapped and an out-of-order
/// delivery window may have occurred for messages sharing a lane.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bounded by construction:</strong> state is <c>long[laneCount]</c> — exactly N entries,
/// fixed at construction. No per-key or per-message allocation (ADR-003, ADR-004). Per-lane
/// granularity is intentionally coarse (different keys sharing a lane merge their epochs), but is
/// sufficient for diagnostic detection of re-map windows (the goal is observability, not correction).
/// See OQ-6 / §9 for the decision rationale.
/// </para>
/// <para>
/// <strong>No-header = no-op (D2):</strong> when <c>BW-MappingEpoch</c> is absent from a message
/// the tracker does nothing. This is the correct path for transports without consistent-hash routing
/// (SAC, non-RabbitMQ adapters, or un-deployed topology).
/// </para>
/// <para>
/// <strong>Security (S2):</strong> the re-map window log uses
/// <see cref="OrderingKeyDiagnostics.ToOpaqueToken"/> — the raw ordering-key value is NEVER logged.
/// When the key is <see langword="null"/> the lane index aggregate is logged instead.
/// </para>
/// </remarks>
internal sealed partial class MappingEpochTracker
{
    /// <summary>
    /// Core-local copy of the AMQP header name that carries the topology-derived mapping epoch.
    /// Must match <c>RabbitMqTransportAdapter.MappingEpochHeaderName</c> exactly (SEC-1 — cross-check
    /// test prevents silent divergence). Core cannot reference the RabbitMQ adapter (NetArchTest
    /// Core↛RabbitMQ), so a local copy is required.
    /// </summary>
    internal const string MappingEpochHeaderName = "BW-MappingEpoch";

    private const long NoEpochSentinel = long.MinValue;

    private readonly long[] _lastEpochPerLane;
    private readonly string _endpointName;
    private readonly ILogger _logger;

    internal MappingEpochTracker(int laneCount, string endpointName, ILogger logger)
    {
        _lastEpochPerLane = new long[laneCount];
        _lastEpochPerLane.AsSpan().Fill(NoEpochSentinel);
        _endpointName = endpointName;
        _logger = logger;
    }

    /// <summary>
    /// Records a mapping-epoch observation for the given lane. On the first observation for a lane
    /// the epoch is stored silently. When the lane's stored epoch differs from <paramref name="epoch"/>
    /// a re-map window Warning is emitted (using <see cref="OrderingKeyDiagnostics.ToOpaqueToken"/>
    /// for the key, never the raw value). Same epoch = no-op. Allocation-free on the hot path.
    /// </summary>
    /// <param name="laneIndex">Index of the lane (0-based, must be &lt; laneCount).</param>
    /// <param name="epoch">Epoch value parsed from the <c>BW-MappingEpoch</c> header.</param>
    /// <param name="orderingKey">
    /// The raw ordering key — used ONLY to produce the opaque token for the log message. Never
    /// stored or forwarded beyond <see cref="OrderingKeyDiagnostics.ToOpaqueToken"/>.
    /// </param>
    internal void Observe(int laneIndex, long epoch, string? orderingKey)
    {
        long previous = _lastEpochPerLane[laneIndex];

        if (previous == NoEpochSentinel)
        {
            // First observation for this lane — store and continue silently.
            _lastEpochPerLane[laneIndex] = epoch;
            return;
        }

        if (previous == epoch)
        {
            // Same epoch — no re-map, hot path.
            return;
        }

        // Epoch changed: emit re-map window warning and update stored epoch.
        string opaqueToken = orderingKey is not null
            ? OrderingKeyDiagnostics.ToOpaqueToken(orderingKey)
            : $"lane:{laneIndex}";

        LogRemapWindowDetected(_logger, _endpointName, opaqueToken);
        _lastEpochPerLane[laneIndex] = epoch;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Endpoint {EndpointName}: consistent-hash re-map window detected for key {OpaqueToken}. " +
                  "Messages with this key may have been delivered out of per-key FIFO order during the " +
                  "topology change. Investigate queue binding changes or broker node restarts.")]
    private static partial void LogRemapWindowDetected(
        ILogger logger, string endpointName, string opaqueToken);
}
