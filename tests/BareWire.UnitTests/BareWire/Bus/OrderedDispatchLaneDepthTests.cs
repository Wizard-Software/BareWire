using AwesomeAssertions;
using BareWire.Bus;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Unit tests for <see cref="OrderedDispatchLaneDepth.Resolve"/> — the pure policy function that
/// computes per-lane channel depth from the global inflight budget and lane count (axis 2, ADR-026 §7).
/// </summary>
public sealed class OrderedDispatchLaneDepthTests
{
    // ── Clamp: degenerate lane count ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Resolve_DegenerateLaneCount_ReturnsAtLeastOne(int laneCount)
    {
        int result = OrderedDispatchLaneDepth.Resolve(laneCount, maxInFlightMessages: 8, configuredDepth: null);

        result.Should().BeGreaterThanOrEqualTo(1,
            "a channel depth of 0 is illegal — the no-deadlock invariant requires depth >= 1");
    }

    // ── Clamp: degenerate budget ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Resolve_DegenerateMaxInFlightMessages_ReturnsAtLeastOne(int maxInFlight)
    {
        int result = OrderedDispatchLaneDepth.Resolve(laneCount: 4, maxInFlightMessages: maxInFlight, configuredDepth: null);

        result.Should().BeGreaterThanOrEqualTo(1,
            "a zero or negative inflight budget must not produce a zero-depth channel");
    }

    // ── Default policy: ceiling division ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 4, 2)]    // ceil(4/2) = 2
    [InlineData(4, 10, 3)]   // ceil(10/4) = 3
    [InlineData(8, 8, 1)]    // ceil(8/8) = 1
    [InlineData(1, 16, 16)]  // single lane — depth equals budget
    [InlineData(3, 7, 3)]    // ceil(7/3) = 3
    public void Resolve_FallbackPolicy_ReturnsCeilingBudgetPerLane(
        int laneCount, int maxInFlight, int expected)
    {
        int result = OrderedDispatchLaneDepth.Resolve(laneCount, maxInFlight, configuredDepth: null);

        result.Should().Be(expected,
            $"default policy: ceil({maxInFlight}/{laneCount}) = {expected}");
    }

    // ── Monotonicity: more lanes → not-greater depth per lane ────────────────────────────────────

    [Fact]
    public void Resolve_MoreLanes_DepthPerLaneNotGreater()
    {
        int budget = 16;
        int depthAt2 = OrderedDispatchLaneDepth.Resolve(laneCount: 2, budget, configuredDepth: null);
        int depthAt4 = OrderedDispatchLaneDepth.Resolve(laneCount: 4, budget, configuredDepth: null);

        depthAt4.Should().BeLessThanOrEqualTo(depthAt2,
            "doubling the lane count halves the per-lane share — depth must be non-increasing");
    }

    // ── configuredDepth: honored when supplied ────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ConfiguredDepthProvided_ReturnsConfiguredValue()
    {
        int result = OrderedDispatchLaneDepth.Resolve(laneCount: 4, maxInFlightMessages: 100, configuredDepth: 7);

        result.Should().Be(7, "an explicit configuredDepth must override the default policy");
    }

    [Fact]
    public void Resolve_ConfiguredDepthZeroOrNegative_ClampsToOne()
    {
        int resultZero = OrderedDispatchLaneDepth.Resolve(laneCount: 4, maxInFlightMessages: 100, configuredDepth: 0);
        int resultNeg = OrderedDispatchLaneDepth.Resolve(laneCount: 4, maxInFlightMessages: 100, configuredDepth: -5);

        resultZero.Should().Be(1,
            "configuredDepth=0 must be clamped to 1 — the no-deadlock invariant requires depth >= 1");
        resultNeg.Should().Be(1,
            "configuredDepth<0 must be clamped to 1 — the no-deadlock invariant requires depth >= 1");
    }
}
