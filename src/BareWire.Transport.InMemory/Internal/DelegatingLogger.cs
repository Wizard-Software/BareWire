using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that forwards every call, unchanged, to an inner untyped
/// <see cref="ILogger"/>. Used to hand the adapter's own logger to a collaborator (<see cref="InMemoryRouter"/>)
/// whose constructor requires a logger typed to itself, without registering a second logger category
/// through dependency injection.
/// </summary>
/// <param name="inner">The logger to forward every call to. Must not be <see langword="null"/>.</param>
internal sealed class DelegatingLogger<T>(ILogger inner) : ILogger<T>
{
    private readonly ILogger _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}
