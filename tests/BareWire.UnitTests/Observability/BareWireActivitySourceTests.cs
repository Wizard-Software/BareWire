using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Observability;

namespace BareWire.UnitTests.Observability;

/// <summary>
/// Unit tests for <see cref="BareWireActivitySource"/> — verifies that spans are created
/// with the correct names, kinds, and OpenTelemetry semantic-convention tags.
/// </summary>
public sealed class BareWireActivitySourceTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _recordedActivities = [];

    public BareWireActivitySourceTests()
    {
        // Register a listener that samples all activities from the "BareWire" source.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BareWire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _recordedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    // ── StartPublish ───────────────────────────────────────────────────────────

    [Fact]
    public void StartPublishActivity_CreatesSpanWithCorrectTags()
    {
        // Arrange
        var messageId = Guid.NewGuid();

        // Act
        using Activity? activity = BareWireActivitySource.StartPublish(
            "OrderCreated",
            "orders-exchange",
            messageId);

        // Assert — span is created and has correct name, kind, and tags
        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("OrderCreated publish");
        activity.Kind.Should().Be(ActivityKind.Producer);
        activity.GetTagItem("messaging.system").Should().Be("barewire");
        activity.GetTagItem("messaging.destination").Should().Be("orders-exchange");
        activity.GetTagItem("messaging.message.id").Should().Be(messageId.ToString());
    }

    [Fact]
    public void StartPublishActivity_WithoutListener_ReturnsNull()
    {
        // Arrange — dispose the sampling listener so no one listens to "BareWire"
        _listener.Dispose();

        // Act
        using Activity? activity = BareWireActivitySource.StartPublish(
            "OrderCreated",
            "orders-exchange",
            Guid.NewGuid());

        // Assert — returns null when no listener is registered (ActivitySource contract)
        activity.Should().BeNull();
    }

    // ── StartConsume ───────────────────────────────────────────────────────────

    [Fact]
    public void StartConsumeActivity_CreatesSpanWithConsumerKind()
    {
        // Arrange
        var messageId = Guid.NewGuid();

        // Act
        using Activity? activity = BareWireActivitySource.StartConsume(
            "OrderCreated",
            "orders-queue",
            messageId,
            default);

        // Assert — kind must be Consumer per OTel messaging conventions
        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("OrderCreated process");
        activity.Kind.Should().Be(ActivityKind.Consumer);
        activity.GetTagItem("messaging.system").Should().Be("barewire");
        activity.GetTagItem("messaging.destination").Should().Be("orders-queue");
        activity.GetTagItem("messaging.consumer.id").Should().Be("orders-queue");
        activity.GetTagItem("messaging.message.id").Should().Be(messageId.ToString());
    }

    // ── StartSagaTransition ────────────────────────────────────────────────────

    [Fact]
    public void StartSagaTransitionActivity_SetsCorrectAttributes()
    {
        // Arrange
        var correlationId = Guid.NewGuid();

        // Act
        using Activity? activity = BareWireActivitySource.StartSagaTransition(
            "OrderSaga",
            "Pending",
            "Processing",
            correlationId);

        // Assert
        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("OrderSaga.Processing transition");
        activity.Kind.Should().Be(ActivityKind.Internal);
        activity.GetTagItem("messaging.system").Should().Be("barewire");
        activity.GetTagItem("saga.correlation_id").Should().Be(correlationId.ToString());
        activity.GetTagItem("saga.state_from").Should().Be("Pending");
        activity.GetTagItem("saga.state_to").Should().Be("Processing");
    }

    // ── Parent-child relationship ──────────────────────────────────────────────

    [Fact]
    public void Activity_ParentChildRelationship_IsPreserved()
    {
        // Arrange — start a parent (publish) activity so it becomes Activity.Current
        using Activity? parent = BareWireActivitySource.StartPublish(
            "OrderCreated",
            "orders-exchange",
            Guid.NewGuid());

        parent.Should().NotBeNull();

        // Build an ActivityContext from the parent to simulate what TraceContextPropagator does
        // when extract is called on the consumer side.
        var parentContext = new ActivityContext(
            parent!.TraceId,
            parent.SpanId,
            parent.ActivityTraceFlags,
            isRemote: true);

        // Act — start a child consume activity with the extracted parent context
        using Activity? child = BareWireActivitySource.StartConsume(
            "OrderCreated",
            "orders-queue",
            Guid.NewGuid(),
            parentContext);

        // Assert — child shares the same trace_id as the parent
        child.Should().NotBeNull();
        child!.TraceId.Should().Be(parent.TraceId);
        child.ParentSpanId.Should().Be(parent.SpanId);
    }

    // ── StartSettle ────────────────────────────────────────────────────────────

    [Fact]
    public void StartSettleActivity_CreatesInternalSpanWithSettlementTag()
    {
        // Act
        using Activity? activity = BareWireActivitySource.StartSettle("orders-queue");

        // Assert
        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("orders-queue settle");
        activity.Kind.Should().Be(ActivityKind.Internal);
        activity.GetTagItem("messaging.settlement.action").Should().Be("settle");
    }
}
