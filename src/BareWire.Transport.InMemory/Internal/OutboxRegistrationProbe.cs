using Microsoft.Extensions.DependencyInjection;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Container-level facts about the transactional outbox and inbox registrations, as reported by
/// <see cref="OutboxRegistrationProbe.Inspect"/>.
/// </summary>
/// <param name="OutboxRegistered">
/// <see langword="true"/> when a service descriptor for the outbox store is present in the container.
/// </param>
/// <param name="InboxRegistered">
/// <see langword="true"/> when a service descriptor for the inbox store is present in the container.
/// </param>
internal readonly record struct OutboxRegistrationState(bool OutboxRegistered, bool InboxRegistered);

/// <summary>
/// Detects whether the BareWire transactional outbox and inbox stores are registered in an
/// <see cref="IServiceCollection"/> — without a compile-time reference to the <c>BareWire.Outbox</c>
/// package, which would violate the in-memory transport's package dependency rule (depends on
/// <c>BareWire.Abstractions</c> only). Detection compares each descriptor's
/// <see cref="ServiceDescriptor.ServiceType"/> full name (ordinal) against the outbox package's store
/// interface names, read once from the descriptor's public <c>ServiceType</c> — never its
/// implementation, which is unsafe to read for a keyed registration.
/// </summary>
internal static class OutboxRegistrationProbe
{
    /// <summary>The full name of <c>BareWire.Outbox.IOutboxStore</c>, matched by <see cref="Inspect"/>.</summary>
    internal const string OutboxStoreServiceTypeName = "BareWire.Outbox.IOutboxStore";

    /// <summary>The full name of <c>BareWire.Outbox.IInboxStore</c>, matched by <see cref="Inspect"/>.</summary>
    internal const string InboxStoreServiceTypeName = "BareWire.Outbox.IInboxStore";

    /// <summary>
    /// Scans <paramref name="services"/> for a service descriptor whose <c>ServiceType</c> full name
    /// matches <see cref="OutboxStoreServiceTypeName"/> or <see cref="InboxStoreServiceTypeName"/>.
    /// </summary>
    /// <param name="services">The service descriptors to scan.</param>
    /// <returns>The outbox/inbox registration facts found in <paramref name="services"/>.</returns>
    internal static OutboxRegistrationState Inspect(IEnumerable<ServiceDescriptor> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        bool outboxRegistered = false;
        bool inboxRegistered = false;

        foreach (ServiceDescriptor descriptor in services)
        {
            string? serviceTypeName = descriptor.ServiceType.FullName;

            if (string.Equals(serviceTypeName, OutboxStoreServiceTypeName, StringComparison.Ordinal))
            {
                outboxRegistered = true;
            }
            else if (string.Equals(serviceTypeName, InboxStoreServiceTypeName, StringComparison.Ordinal))
            {
                inboxRegistered = true;
            }

            if (outboxRegistered && inboxRegistered)
            {
                break;
            }
        }

        return new OutboxRegistrationState(outboxRegistered, inboxRegistered);
    }
}
