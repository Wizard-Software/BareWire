namespace BareWire.Abstractions.Pipeline;

/// <summary>
/// Well-known keys for <see cref="MessageContext.Items"/>.
/// </summary>
public static class WellKnownItemKeys
{
    /// <summary>Inbox deduplication filter skipped this message as a duplicate.</summary>
    public const string InboxFiltered = "inbox:filtered";
}
