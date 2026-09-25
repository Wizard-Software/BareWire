namespace BareWire.Samples.InMemoryModularMonolith.Messaging;

/// <summary>
/// The transport backing the modular monolith. Switching between the two is a registration change
/// only — see <see cref="TransportRegistration.AddModulithTransport"/> — the topology, the module
/// code, and the consumers are identical for both values.
/// </summary>
public enum TransportKind
{
    /// <summary>The in-memory transport (default). At-most-once delivery, single process, no broker.</summary>
    InMemory,

    /// <summary>The RabbitMQ transport. At-least-once delivery via the transactional outbox.</summary>
    RabbitMQ,
}

/// <summary>
/// Parses the <c>Transport</c> configuration key into a <see cref="TransportKind"/>.
/// </summary>
internal static class TransportKindParser
{
    /// <summary>
    /// Parses <paramref name="value"/> into a <see cref="TransportKind"/>. A <see langword="null"/> or
    /// whitespace-only value defaults to <see cref="TransportKind.InMemory"/>. Parsing is
    /// case-insensitive. An unrecognized value fails fast with <see cref="InvalidOperationException"/>
    /// so a typo in configuration is caught at startup, not silently ignored.
    /// </summary>
    /// <param name="value">The raw <c>Transport</c> configuration value.</param>
    /// <returns>The parsed <see cref="TransportKind"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="value"/> does not match any <see cref="TransportKind"/> member.
    /// </exception>
    public static TransportKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TransportKind.InMemory;
        }

        if (Enum.TryParse(value, ignoreCase: true, out TransportKind parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Unknown Transport value '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<TransportKind>())}.");
    }
}
