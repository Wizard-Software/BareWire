namespace BareWire.Abstractions.Pipeline;

/// <summary>
/// Well-known keys for <see cref="MessageContext.Items"/>.
/// </summary>
public static class WellKnownItemKeys
{
    /// <summary>Inbox deduplication filter skipped this message as a duplicate.</summary>
    public const string InboxFiltered = "inbox:filtered";

    /// <summary>
    /// The value stored under this key in <see cref="MessageContext.Items"/> is an <see cref="Action"/>
    /// that the retry middleware invokes before every retry attempt (never before the first attempt).
    /// It lets outer middleware, such as the transactional or in-memory outbox, discard side effects buffered
    /// by the failed attempt so they are not duplicated by the next one.
    /// This is a single slot owned by the outbox middleware (transactional or in-memory) when one is present:
    /// other middleware must not overwrite or remove it, and callbacks stored here must not throw.
    /// </summary>
    public const string RetryAttemptStarting = "retry:attempt-starting";
}
