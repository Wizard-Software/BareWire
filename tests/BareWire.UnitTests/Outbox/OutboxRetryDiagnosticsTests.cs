using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

// OutboxRetryDiagnostics: rate-limited Warning log + counter + lazily-sampled gauge for outbox rows
// released for a deferred retry. Each test uses its own uniquely-named Meter so MeterListener
// callbacks from other tests in the suite never cross-contaminate measurements.
public sealed class OutboxRetryDiagnosticsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly CapturingLogger _logger = new();
    private readonly FakeTimeProvider _clock = new(T0);
    private readonly Meter _meter = new("BareWire.Test." + Guid.NewGuid());
    private readonly MeterListener _listener;

    private readonly List<(string InstrumentName, long Value, KeyValuePair<string, object?>[] Tags)> _longMeasurements = [];
    private readonly List<(string InstrumentName, double Value, KeyValuePair<string, object?>[] Tags)> _doubleMeasurements = [];

    public OutboxRetryDiagnosticsTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            _longMeasurements.Add((instrument.Name, measurement, tags.ToArray())));

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            _doubleMeasurements.Add((instrument.Name, measurement, tags.ToArray())));

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meter.Dispose();
    }

    private OutboxRetryDiagnostics CreateSut(bool withMeter = true)
        => new(_logger, _clock, withMeter ? _meter : null);

    [Fact]
    public void RowsReleasedForRetry_FirstCall_LogsWarningWithRowIdAndRetryCount()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.RowsReleasedForRetry(retriedCount: 2, rowId: 42, retryCount: 3);

        _logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        IReadOnlyList<KeyValuePair<string, object?>> state = _logger.Events.Single().State;
        state.Should().Contain(kv => kv.Key == "RowId" && Equals(kv.Value, 42L));
        state.Should().Contain(kv => kv.Key == "RetryCount" && Equals(kv.Value, 3));
        state.Should().Contain(kv => kv.Key == "RetriedCount" && Equals(kv.Value, 2));
        state.Should().Contain(kv => kv.Key == "SuppressedCount" && Equals(kv.Value, 0L));
    }

    [Fact]
    public void RowsReleasedForRetry_SeriesWithinWindow_LogsOnceAndCountsSuppressedRows()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        for (int i = 0; i < 5; i++)
        {
            sut.RowsReleasedForRetry(retriedCount: 1, rowId: i, retryCount: 1);
        }

        _clock.Advance(OutboxRetryDiagnostics.LogWindow);
        sut.RowsReleasedForRetry(retriedCount: 1, rowId: 99, retryCount: 1);

        var warnings = _logger.Events.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(2);
        warnings[^1].State.Should().Contain(kv => kv.Key == "SuppressedCount" && Equals(kv.Value, 4L));
    }

    [Fact]
    public void RowsReleasedForRetry_JustBeforeWindowElapses_StillSuppresses()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.RowsReleasedForRetry(retriedCount: 1, rowId: 1, retryCount: 1);
        _clock.Advance(OutboxRetryDiagnostics.LogWindow - TimeSpan.FromTicks(1));
        sut.RowsReleasedForRetry(retriedCount: 1, rowId: 2, retryCount: 1);

        _logger.Events.Count(e => e.Level == LogLevel.Warning).Should().Be(
            1, "the window has not yet strictly elapsed");
    }

    [Fact]
    public void RowsReleasedForRetry_WithMeter_IncrementsRetriedRowsCounterByRetriedCount()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.RowsReleasedForRetry(retriedCount: 3, rowId: 1, retryCount: 1);
        sut.RowsReleasedForRetry(retriedCount: 2, rowId: 2, retryCount: 1);

        _longMeasurements
            .Where(m => m.InstrumentName == OutboxRetryDiagnostics.RetriedRowsCounterName)
            .Sum(m => m.Value)
            .Should().Be(5);
        sut.RetriedRowCount.Should().Be(5);
    }

    [Fact]
    public void RowsReleasedForRetry_WithMeter_RecordsCounterWithoutTags()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.RowsReleasedForRetry(retriedCount: 1, rowId: 42, retryCount: 3);

        _longMeasurements
            .Where(m => m.InstrumentName == OutboxRetryDiagnostics.RetriedRowsCounterName)
            .Should().ContainSingle().Which.Tags.Should().BeEmpty();
    }

    [Fact]
    public void OldestDueRetryAgeGauge_AfterRecord_ReportsMeasurementWithoutTags()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.RecordOldestDueRetry(_clock.GetUtcNow() - TimeSpan.FromSeconds(5));
        _listener.RecordObservableInstruments();

        _doubleMeasurements
            .Where(m => m.InstrumentName == OutboxRetryDiagnostics.OldestDueRetryAgeGaugeName)
            .Should().ContainSingle().Which.Tags.Should().BeEmpty();
    }

    [Fact]
    public void OldestDueRetryAgeGauge_WhenLaterSampleHasNoDueRetry_ReportsZero()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.RecordOldestDueRetry(_clock.GetUtcNow() - TimeSpan.FromSeconds(30));
        sut.RecordOldestDueRetry(null);
        _listener.RecordObservableInstruments();

        _doubleMeasurements
            .Where(m => m.InstrumentName == OutboxRetryDiagnostics.OldestDueRetryAgeGaugeName)
            .Select(m => m.Value)
            .Should().ContainSingle().Which.Should().Be(0.0);
    }

    [Fact]
    public void RowsReleasedForRetry_LogState_ContainsNoInstanceOrPayloadFields()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.RowsReleasedForRetry(retriedCount: 1, rowId: 7, retryCount: 2);

        (LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State) entry = _logger.Events.Single();
        entry.State.Select(kv => kv.Key).Should().BeSubsetOf(
            ["RowId", "RetryCount", "RetriedCount", "SuppressedCount", "{OriginalFormat}"]);
        entry.Message.Should().NotContain("LockedBy");
    }

    [Fact]
    public void TryConsumeAgeSampleRequest_BeforeGaugeObserved_ReturnsFalse()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.TryConsumeAgeSampleRequest().Should().BeFalse();
    }

    [Fact]
    public void TryConsumeAgeSampleRequest_AfterGaugeObserved_ReturnsTrueOnce()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        _listener.RecordObservableInstruments();

        sut.TryConsumeAgeSampleRequest().Should().BeTrue();
        sut.TryConsumeAgeSampleRequest().Should().BeFalse();
    }

    [Fact]
    public void OldestDueRetryAgeGauge_AfterRecord_ReportsAgeInSeconds()
    {
        OutboxRetryDiagnostics sut = CreateSut();
        DateTimeOffset dueAt = _clock.GetUtcNow() - TimeSpan.FromSeconds(90);

        sut.RecordOldestDueRetry(dueAt);
        _clock.Advance(TimeSpan.FromSeconds(10));
        _listener.RecordObservableInstruments();

        _doubleMeasurements
            .Where(m => m.InstrumentName == OutboxRetryDiagnostics.OldestDueRetryAgeGaugeName)
            .Select(m => m.Value)
            .Should().ContainSingle().Which.Should().Be(100.0);
    }

    [Fact]
    public void OldestDueRetryAgeGauge_WhenNoDueRetry_ReportsZero()
    {
        OutboxRetryDiagnostics sut = CreateSut();

        sut.RecordOldestDueRetry(null);
        _listener.RecordObservableInstruments();

        _doubleMeasurements
            .Where(m => m.InstrumentName == OutboxRetryDiagnostics.OldestDueRetryAgeGaugeName)
            .Select(m => m.Value)
            .Should().ContainSingle().Which.Should().Be(0.0);
    }

    [Fact]
    public void OldestDueRetryAgeGauge_BeforeFirstSample_ReportsNoMeasurement()
    {
        CreateSut();

        _listener.RecordObservableInstruments();

        _doubleMeasurements
            .Where(m => m.InstrumentName == OutboxRetryDiagnostics.OldestDueRetryAgeGaugeName)
            .Should().BeEmpty();
    }

    [Fact]
    public void TryConsumeAgeSampleRequest_WithoutMeter_ReturnsFalse()
    {
        OutboxRetryDiagnostics sut = CreateSut(withMeter: false);

        _listener.RecordObservableInstruments(); // no gauge exists without a meter — nothing to observe
        sut.TryConsumeAgeSampleRequest().Should().BeFalse();
    }

    // Minimal ILogger that captures both the formatted message and the structured log state, so tests
    // can assert on individual named fields (RowId, RetryCount, ...) as well as the rendered text.
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var kvps = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? throw new InvalidOperationException("Expected structured log state implementing IReadOnlyList<KeyValuePair<string, object?>>.");
            Events.Add((logLevel, formatter(state, exception), kvps));
        }
    }
}
