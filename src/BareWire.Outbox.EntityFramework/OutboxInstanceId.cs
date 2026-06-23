namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// Strongly-typed wrapper around the per-process outbox dispatcher instance identifier.
/// Registered as a Singleton in DI to avoid ambiguity with other <see cref="string"/> registrations.
/// </summary>
internal sealed record OutboxInstanceId(string Value);
