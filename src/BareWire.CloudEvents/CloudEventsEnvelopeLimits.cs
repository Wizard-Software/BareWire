namespace BareWire.CloudEvents;

/// <summary>
/// Immutable, bounded configuration for CloudEvents structured-mode envelope hardening.
/// All limits are enforced fail-fast before any costly deserialization work (SEC-1/ADR-003).
/// </summary>
/// <remarks>
/// <para>
/// Every limit must be strictly positive. A zero or negative limit would open a DoS vector
/// (zero max size accepts nothing; the validator guards against it at construction time).
/// </para>
/// <para>
/// Use <see cref="Default"/> for standard deployments. Pass a custom instance to
/// <c>CloudEventsEnvelopeDeserializer(CloudEventsEnvelopeLimits)</c> in tests or where tighter
/// per-endpoint hardening is required.
/// </para>
/// </remarks>
internal sealed record CloudEventsEnvelopeLimits
{
    /// <summary>
    /// The default bounded limits suitable for most CloudEvents 1.0 workloads.
    /// </summary>
    /// <value>
    /// <list type="bullet">
    /// <item><description><see cref="MaxEnvelopeSizeBytes"/> = 262144 (256 KiB)</description></item>
    /// <item><description><see cref="MaxAttributeCount"/> = 64</description></item>
    /// <item><description><see cref="MaxAttributeValueLength"/> = 4096</description></item>
    /// <item><description><see cref="MaxExtensionNameLength"/> = 64</description></item>
    /// <item><description><see cref="MaxDataDepth"/> = 32</description></item>
    /// </list>
    /// </value>
    internal static readonly CloudEventsEnvelopeLimits Default = new(
        maxEnvelopeSizeBytes: 262144,
        maxAttributeCount: 64,
        maxAttributeValueLength: 4096,
        maxExtensionNameLength: 64,
        maxDataDepth: 32);

    /// <summary>
    /// Gets the maximum total byte length of the envelope (Rule 1 — SEC-1).
    /// Envelopes larger than this value are rejected before any JSON parsing begins.
    /// Default: 262144 (256 KiB).
    /// </summary>
    internal int MaxEnvelopeSizeBytes { get; }

    /// <summary>
    /// Gets the maximum number of top-level JSON properties in the envelope (Rule 2 — SEC-1).
    /// Counts all CE context attributes including extensions and <c>data</c>.
    /// Default: 64.
    /// </summary>
    internal int MaxAttributeCount { get; }

    /// <summary>
    /// Gets the maximum byte length of any single scalar attribute value (Rule 3 — SEC-1).
    /// Applies to <c>String</c> and <c>Number</c> tokens; the <c>data</c> field is exempt
    /// (covered by <see cref="MaxEnvelopeSizeBytes"/>). For non-scalar extension values the
    /// subtree byte size is compared against this limit (SEC-2 mitigation).
    /// Default: 4096.
    /// </summary>
    internal int MaxAttributeValueLength { get; }

    /// <summary>
    /// Gets the maximum byte length of an extension attribute name (Rule 4 / SEC-3).
    /// CE 1.0 recommends 1–20 characters; this limit uses 64 for operational flexibility.
    /// Default: 64.
    /// </summary>
    internal int MaxExtensionNameLength { get; }

    /// <summary>
    /// Gets the maximum JSON nesting depth for the <c>data</c> payload (SEC-1).
    /// Used as <see cref="System.Text.Json.JsonSerializerOptions.MaxDepth"/> on the bounded
    /// options copy. A deeply-nested <c>data</c> value below <see cref="MaxEnvelopeSizeBytes"/>
    /// can still cause CPU/allocation blow-up without this guard.
    /// Default: 32.
    /// </summary>
    internal int MaxDataDepth { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="CloudEventsEnvelopeLimits"/> with all limits
    /// explicitly specified. Every parameter must be strictly positive.
    /// </summary>
    /// <param name="maxEnvelopeSizeBytes">Maximum envelope size in bytes. Must be &gt; 0.</param>
    /// <param name="maxAttributeCount">Maximum attribute count. Must be &gt; 0.</param>
    /// <param name="maxAttributeValueLength">Maximum attribute value length in bytes. Must be &gt; 0.</param>
    /// <param name="maxExtensionNameLength">Maximum extension name length in bytes. Must be &gt; 0.</param>
    /// <param name="maxDataDepth">Maximum data nesting depth. Must be &gt; 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if any parameter is zero or negative.
    /// </exception>
    internal CloudEventsEnvelopeLimits(
        int maxEnvelopeSizeBytes,
        int maxAttributeCount,
        int maxAttributeValueLength,
        int maxExtensionNameLength,
        int maxDataDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEnvelopeSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttributeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttributeValueLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExtensionNameLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDataDepth);

        MaxEnvelopeSizeBytes = maxEnvelopeSizeBytes;
        MaxAttributeCount = maxAttributeCount;
        MaxAttributeValueLength = maxAttributeValueLength;
        MaxExtensionNameLength = maxExtensionNameLength;
        MaxDataDepth = maxDataDepth;
    }
}
