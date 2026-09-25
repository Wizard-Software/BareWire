using Microsoft.Extensions.Logging;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// A thread-safe <see cref="ILogger{TCategoryName}"/> that captures every entry logged through it —
/// level, <see cref="EventId"/>, the formatted message, and the structured state — so a test can assert
/// exactly which entries (including by <see cref="EventId"/>) were logged, without depending on message
/// text. Shared across the in-memory transport's test files; a test that needs its own throwing logger
/// still defines that separately (see e.g. <c>ThrowingLogger</c> in the send/metrics tests).
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];
    private readonly Lock _lock = new();

    /// <summary>Gets every entry logged through this instance so far, in logging order.</summary>
    internal IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        IReadOnlyList<KeyValuePair<string, object?>> structuredState =
            state is IReadOnlyList<KeyValuePair<string, object?>> pairs ? pairs : [];

        lock (_lock)
        {
            _entries.Add(new LogEntry(logLevel, eventId, message, structuredState, exception));
        }
    }
}

/// <summary>One entry captured by <see cref="CapturingLogger{T}"/>.</summary>
/// <param name="Level">The log level the entry was logged at.</param>
/// <param name="EventId">The entry's event id — the recommended way to assert on a specific log call.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="State">
/// The entry's structured state — every named field a <c>[LoggerMessage]</c>-generated call passes, as
/// key/value pairs. Empty when the logged state does not implement
/// <see cref="IReadOnlyList{T}"/> of <see cref="KeyValuePair{TKey, TValue}"/> (never the case for
/// source-generated <c>LoggerMessage</c> calls).
/// </param>
/// <param name="Exception">The exception passed to the log call, if any.</param>
internal sealed record LogEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> State,
    Exception? Exception)
{
    /// <summary>
    /// Looks up a named field in <see cref="State"/> (for example <c>"RejectedCount"</c> or
    /// <c>"DurationMs"</c>), as a source-generated <c>[LoggerMessage]</c> call names each parameter after
    /// itself. Throws <see cref="KeyNotFoundException"/> when no such field was logged.
    /// </summary>
    internal object? this[string key] =>
        State.First(pair => pair.Key == key).Value;
}
