using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>A single captured log entry: category, level, event id, and the fully formatted message (including any exception text).</summary>
internal sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message);

/// <summary>
/// Captures log entries and "BareWire" meter counter measurements for one <see cref="InMemoryBusHost"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Meter scoping.</strong> The <c>"BareWire"</c> meter is created fresh by every host's
/// <c>IMeterFactory</c> (hosts are one-per-test, private containers), so a process-wide
/// <see cref="MeterListener"/> would also observe another host's instruments if one happened to be
/// running concurrently. <see cref="StartListening"/> filters strictly to instruments whose
/// <c>Meter.Scope</c> is reference-equal to the supplied <see cref="IMeterFactory"/>, so only this
/// host's own measurements are ever recorded.
/// </para>
/// <para>
/// <strong>Counters only.</strong> Every instrument this type owns
/// (<see cref="InMemoryTransportMetrics.RejectedCounterName"/>,
/// <see cref="InMemoryTransportMetrics.LatchEpisodesCounterName"/>) is a <see cref="Counter{T}"/> of
/// <see langword="long"/> — summed here. The three observable gauges
/// (<c>occupancy</c>/<c>capacity</c>/<c>latched</c>) are point-in-time queue state, better read
/// directly off <c>InMemoryBroker.TryGetQueue</c> / <c>InMemoryQueue.IsLatched</c> (visible via
/// <c>InternalsVisibleTo</c>) than polled through a gauge callback — this type does not subscribe to
/// them.
/// </para>
/// </remarks>
internal sealed class InMemoryBusTelemetry : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _logs = new();
    private readonly ConcurrentBag<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _counterMeasurements = new();
    private MeterListener? _meterListener;
    private bool _disposed;

    /// <summary>Gets a snapshot of every log entry captured so far, in no particular order.</summary>
    public IReadOnlyList<CapturedLog> Logs => [.. _logs];

    /// <summary>
    /// Starts observing every <c>"barewire.inmemory.*"</c> counter on the <c>"BareWire"</c> meter
    /// created by <paramref name="meterFactory"/>. Call this BEFORE the transport adapter is
    /// constructed (before <c>IBusControl.StartAsync</c>) so no early measurement is missed.
    /// </summary>
    internal void StartListening(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == InMemoryTransportMetrics.MeterName
                    && ReferenceEquals(instrument.Meter.Scope, meterFactory)
                    && instrument.Name.StartsWith("barewire.inmemory.", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            _counterMeasurements.Add((instrument.Name, measurement, tags.ToArray())));

        listener.Start();
        _meterListener = listener;
    }

    /// <summary>
    /// Sums every counter measurement recorded for <paramref name="instrumentName"/> whose tags
    /// match all of <paramref name="tags"/> (a measurement may carry additional, unfiltered tags).
    /// Passing no <paramref name="tags"/> sums across every tag combination for that instrument.
    /// </summary>
    public long CounterTotal(string instrumentName, params (string Key, string Value)[] tags)
    {
        ArgumentNullException.ThrowIfNull(instrumentName);

        long total = 0;
        foreach ((string instrument, long value, KeyValuePair<string, object?>[] measurementTags) in _counterMeasurements)
        {
            if (instrument == instrumentName && MatchesAllTags(measurementTags, tags))
            {
                total += value;
            }
        }

        return total;
    }

    /// <summary>Returns whether any captured log at or above <paramref name="minimumLevel"/> contains <paramref name="containsText"/>.</summary>
    public bool HasLog(LogLevel minimumLevel, string containsText)
    {
        ArgumentNullException.ThrowIfNull(containsText);
        return _logs.Any(l => l.Level >= minimumLevel && l.Message.Contains(containsText, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _logs);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meterListener?.Dispose();
    }

    private static bool MatchesAllTags(
        ReadOnlySpan<KeyValuePair<string, object?>> measurementTags, (string Key, string Value)[] filters)
    {
        foreach ((string key, string value) in filters)
        {
            bool found = false;
            foreach (KeyValuePair<string, object?> tag in measurementTags)
            {
                if (tag.Key == key && string.Equals(tag.Value?.ToString(), value, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An <see cref="ILogger"/> that appends every formatted message (plus exception text) to the shared capture queue. Logs at every level — filtering happens at the <c>ILoggingBuilder.SetMinimumLevel</c> call site.</summary>
    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            string message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message} {exception}";
            }

            logs.Enqueue(new CapturedLog(category, logLevel, eventId, message));
        }
    }
}
