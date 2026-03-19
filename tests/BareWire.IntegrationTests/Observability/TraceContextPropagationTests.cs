using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Observability;

namespace BareWire.IntegrationTests.Observability;

/// <summary>
/// Component-level integration tests for W3C TraceContext propagation via
/// <see cref="TraceContextPropagator"/> and <see cref="BareWireActivitySource"/>.
/// Tests verify that trace identity (trace_id, parent_id) is correctly injected into
/// outbound headers and extracted to produce a correlated child span on the consumer side.
/// No external broker is required — propagation is tested at the component level.
/// </summary>
public sealed class TraceContextPropagationTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _startedActivities = [];

    public TraceContextPropagationTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BareWire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _startedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Simulates the publish side: creates a publish activity, injects its trace context
    /// into a headers dictionary, then stops the activity.
    /// Returns the activity and the populated headers.
    /// </summary>
    private static (Activity Activity, Dictionary<string, string> Headers) SimulatePublish(
        string messageType,
        string destination,
        Guid messageId)
    {
        var activity = BareWireActivitySource.StartPublish(messageType, destination, messageId)!;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        TraceContextPropagator.InjectTraceContext(activity, headers);
        return (activity, headers);
    }

    /// <summary>
    /// Simulates the consume side: extracts trace context from headers and starts a
    /// child consume activity with the propagated parent context.
    /// </summary>
    private static Activity? SimulateConsume(
        IReadOnlyDictionary<string, string> headers,
        string messageType,
        string endpoint,
        Guid messageId)
    {
        DistributedContextPropagator.Current.ExtractTraceIdAndState(
            headers,
            static (carrier, key, out value, out values) =>
            {
                var dict = (IReadOnlyDictionary<string, string>)carrier!;
                value = dict.TryGetValue(key, out var v) ? v : null;
                values = null;
            },
            out var traceParent,
            out var traceState);

        ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parentContext);
        return BareWireActivitySource.StartConsume(messageType, endpoint, messageId, parentContext);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PublishConsume_PropagatesTraceId_AcrossTransport()
    {
        // Arrange — simulate publishing a message and capturing the outbound headers
        var messageId = Guid.NewGuid();
        var (publishActivity, headers) = SimulatePublish("OrderCreated", "orders-exchange", messageId);

        // Assert — traceparent header was injected
        headers.Should().ContainKey("traceparent");

        // Act — simulate consuming the message on the other side
        using Activity? consumeActivity = SimulateConsume(
            headers,
            "OrderCreated",
            "orders-queue",
            messageId);

        // Assert — consumer span carries the same trace_id as the publisher span
        consumeActivity.Should().NotBeNull();
        consumeActivity!.TraceId.Should().Be(publishActivity.TraceId);

        publishActivity.Dispose();
    }

    [Fact]
    public void PublishConsume_CreatesParentChildSpans()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var (publishActivity, headers) = SimulatePublish("OrderCreated", "orders-exchange", messageId);

        // Act
        using Activity? consumeActivity = SimulateConsume(
            headers,
            "OrderCreated",
            "orders-queue",
            messageId);

        // Assert — the consumer span's parent span id equals the publisher span id,
        // establishing a proper parent-child relationship across the transport boundary.
        consumeActivity.Should().NotBeNull();
        consumeActivity!.ParentSpanId.Should().Be(publishActivity.SpanId);

        publishActivity.Dispose();
    }

    [Fact]
    public void ConsumeWithoutTraceparent_CreatesNewTrace()
    {
        // Arrange — empty headers simulate a message that was published without tracing
        var emptyHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        var messageId = Guid.NewGuid();

        // Act
        using Activity? consumeActivity = SimulateConsume(
            emptyHeaders,
            "OrderCreated",
            "orders-queue",
            messageId);

        // Assert — a fresh trace is created (not attached to any previous span)
        consumeActivity.Should().NotBeNull();
        // ParentSpanId is default (all zeros) when there is no parent
        consumeActivity!.ParentSpanId.Should().Be(default(ActivitySpanId));
    }

    [Fact]
    public void InjectTraceContext_PopulatesTraceparentHeader_WithW3CFormat()
    {
        // Arrange
        using Activity? publishActivity = BareWireActivitySource.StartPublish(
            "OrderCreated",
            "orders-exchange",
            Guid.NewGuid());

        publishActivity.Should().NotBeNull();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        // Act
        TraceContextPropagator.InjectTraceContext(publishActivity, headers);

        // Assert — W3C traceparent format: {version}-{trace-id}-{parent-id}-{flags}
        headers.Should().ContainKey("traceparent");
        var traceparent = headers["traceparent"];
        traceparent.Should().MatchRegex(
            @"^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$",
            "traceparent must follow W3C TraceContext format");
    }

    [Fact]
    public void InjectTraceContext_WithNullActivity_DoesNotPopulateHeaders()
    {
        // Arrange
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        // Act — null activity must be handled gracefully (no exception, no header)
        TraceContextPropagator.InjectTraceContext(null, headers);

        // Assert
        headers.Should().BeEmpty();
    }
}
