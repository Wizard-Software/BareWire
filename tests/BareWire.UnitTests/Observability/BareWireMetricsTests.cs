using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Observability;
using NSubstitute;

namespace BareWire.UnitTests.Observability;

/// <summary>
/// Unit tests for <see cref="BareWireMetrics"/> — verifies that counters, histograms,
/// and tags are recorded correctly via <see cref="MeterListener"/>.
/// </summary>
public sealed class BareWireMetricsTests : IDisposable
{
    // ── Infrastructure ────────────────────────────────────────────────────────

    // Use a real Meter so that MeterListener can capture actual measurements.
    private readonly Meter _meter;
    private readonly BareWireMetrics _metrics;

    // Captured measurements recorded by the MeterListener callbacks.
    private readonly List<(string InstrumentName, long Value, KeyValuePair<string, object?>[] Tags)> _longMeasurements = [];
    private readonly List<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)> _doubleMeasurements = [];

    private readonly MeterListener _listener;

    public BareWireMetricsTests()
    {
        // Provide a real Meter via a fake IMeterFactory so BareWireMetrics can create
        // instruments, and MeterListener can capture measurements synchronously.
        _meter = new Meter("BareWire");

        var meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(_meter);

        _metrics = new BareWireMetrics(meterFactory);

        // Configure MeterListener to capture all BareWire measurements.
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "BareWire")
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _longMeasurements.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            _doubleMeasurements.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
        _meter.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private long SumLong(string instrumentName) =>
        _longMeasurements
            .Where(m => m.InstrumentName == instrumentName)
            .Sum(m => m.Value);

    private double SumDouble(string instrumentName) =>
        _doubleMeasurements
            .Where(m => m.InstrumentName == instrumentName)
            .Sum(m => m.Value);

    // ── RecordPublish ─────────────────────────────────────────────────────────

    [Fact]
    public void RecordPublish_IncrementsPublishedCounter()
    {
        // Act
        _metrics.RecordPublish("orders-queue", "OrderCreated", messageSize: 256);

        // Assert
        SumLong("barewire.messages.published").Should().Be(1);
    }

    [Fact]
    public void RecordPublish_RecordsSizeHistogram()
    {
        // Act
        _metrics.RecordPublish("orders-queue", "OrderCreated", messageSize: 512);

        // Assert — size histogram is recorded with the payload byte count
        _longMeasurements
            .Where(m => m.InstrumentName == "barewire.message.size")
            .Sum(m => m.Value)
            .Should().Be(512);
    }

    // ── RecordConsume ─────────────────────────────────────────────────────────

    [Fact]
    public void RecordConsume_IncrementsConsumedCounter()
    {
        // Act
        _metrics.RecordConsume("orders-queue", "OrderCreated", durationMs: 10.0, messageSize: 256);

        // Assert
        SumLong("barewire.messages.consumed").Should().Be(1);
    }

    [Fact]
    public void RecordConsume_RecordsDurationHistogram()
    {
        // Act
        _metrics.RecordConsume("orders-queue", "OrderCreated", durationMs: 42.5, messageSize: 256);

        // Assert
        SumDouble("barewire.message.duration").Should().Be(42.5);
    }

    [Fact]
    public void RecordConsume_RecordsSizeHistogram()
    {
        // Act
        _metrics.RecordConsume("orders-queue", "OrderCreated", durationMs: 10.0, messageSize: 1024);

        // Assert
        _longMeasurements
            .Where(m => m.InstrumentName == "barewire.message.size")
            .Sum(m => m.Value)
            .Should().Be(1024);
    }

    // ── RecordFailure ─────────────────────────────────────────────────────────

    [Fact]
    public void RecordFailure_IncrementsFailedCounter()
    {
        // Act
        _metrics.RecordFailure("orders-queue", "OrderCreated", "InvalidOperationException");

        // Assert
        SumLong("barewire.messages.failed").Should().Be(1);
    }

    // ── RecordDeadLetter ──────────────────────────────────────────────────────

    [Fact]
    public void RecordDeadLetter_IncrementsDeadLetteredCounter()
    {
        // Act
        _metrics.RecordDeadLetter("orders-queue", "OrderCreated");

        // Assert
        SumLong("barewire.messages.dead_lettered").Should().Be(1);
    }

    // ── Tags ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Metrics_IncludeTags_EndpointAndMessageType()
    {
        // Act
        _metrics.RecordPublish("payments-queue", "PaymentRequested", messageSize: 128);

        // Assert — the published counter measurement carries endpoint and message_type tags
        var measurement = _longMeasurements
            .Single(m => m.InstrumentName == "barewire.messages.published");

        var tagDict = measurement.Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString());
        tagDict["endpoint"].Should().Be("payments-queue");
        tagDict["message_type"].Should().Be("PaymentRequested");
    }

    [Fact]
    public void RecordFailure_IncludesErrorTypeTag()
    {
        // Act
        _metrics.RecordFailure("orders-queue", "OrderCreated", "TimeoutException");

        // Assert — failure counter includes error_type tag
        var measurement = _longMeasurements
            .Single(m => m.InstrumentName == "barewire.messages.failed");

        var tagDict = measurement.Tags.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString());
        tagDict["error_type"].Should().Be("TimeoutException");
    }

    // ── Multiple increments ───────────────────────────────────────────────────

    [Fact]
    public void RecordConsume_CalledMultipleTimes_AccumulatesCorrectly()
    {
        // Act
        _metrics.RecordConsume("orders-queue", "OrderCreated", durationMs: 10.0, messageSize: 100);
        _metrics.RecordConsume("orders-queue", "OrderCreated", durationMs: 20.0, messageSize: 200);
        _metrics.RecordConsume("orders-queue", "OrderCreated", durationMs: 30.0, messageSize: 300);

        // Assert
        SumLong("barewire.messages.consumed").Should().Be(3);
        SumDouble("barewire.message.duration").Should().Be(60.0);
    }
}
