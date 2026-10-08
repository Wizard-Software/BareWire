namespace BareWire.Pipeline;

/// <summary>
/// Keys for <see cref="BareWire.Abstractions.Pipeline.MessageContext.Items"/> used only inside BareWire.
/// They are intentionally not part of the public well-known keys.
/// </summary>
internal static class InternalItemKeys
{
    /// <summary>
    /// Set on the failure path only, after a consumer-level retry policy has been exhausted, so that the
    /// endpoint-level retry does not re-run the consumer.
    /// </summary>
    internal const string ConsumerRetryExhausted = "retry:consumer-exhausted";

    /// <summary>
    /// Failure path only: a deserialization exception captured inside the consumer retry pipeline and
    /// rethrown after it, outside the exhausted-marker handling.
    /// </summary>
    internal const string ConsumerRetryCapturedException = "retry:consumer-captured-exception";
}
