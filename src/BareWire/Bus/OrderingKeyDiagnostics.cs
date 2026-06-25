using System.Security.Cryptography;
using System.Text;
using BareWire.Abstractions.Exceptions;

namespace BareWire.Bus;

/// <summary>
/// The single sanctioned point for touching an ordering-key value in a diagnostic context
/// (ADR-026 §NIE WOLNO — S1/S2). Centralises the SEC convention for the ordering path
/// (R8.9–R8.13) so that no caller is ever tempted to embed a raw key value in an exception
/// message, log line, or metric dimension.
/// </summary>
/// <remarks>
/// <para>
/// <strong>S1 — key value never in <c>OptionValue</c> / message / exception text.</strong>
/// The ordering key (<c>OrderedByHeader</c> / <c>OrderedBy(selector)</c> / correlation-id) is
/// potential PII (e.g. <c>customer-id</c>, <c>account-number</c>, an e-mail carried in a
/// correlation-id). Any fail-fast ordering-configuration error MUST be built via
/// <see cref="OrderingConfigError"/>, which structurally forces
/// <see cref="BareWireConfigurationException.OptionValue"/> to <see langword="null"/> and renders
/// a selector only as the constant placeholder <see cref="SelectorPlaceholder"/> — never the
/// post-evaluation value. Header names are operator-supplied <em>configuration</em>, not message
/// data, and are safe to surface.
/// </para>
/// <para>
/// <strong>S2 — diagnostic correlation uses an opaque token, never the raw value.</strong>
/// A future gap-log (R8.12 poison head parking) and any metric MUST correlate on the opaque token
/// produced by <see cref="ToOpaqueToken"/>, not on the raw key, and MUST NOT introduce a per-key
/// metric dimension (PII + metric-cardinality explosion). Aggregates (lane depth, active lane count,
/// park count) carry no key dimension.
/// </para>
/// <para>
/// <strong>The token is opaque, not a cryptographic anonymizer.</strong>
/// <see cref="ToOpaqueToken"/> is an unsalted, truncated SHA-256 hex digest intended solely for
/// log correlation. It is opaque (not human-readable at a glance) but it is NOT a defence against
/// brute-force recovery of a low-entropy key (a <c>customer-id</c>, e-mail, or account number can
/// be confirmed by dictionary/rainbow-table attack against the digest). Treat the token as a
/// correlation handle only; do not rely on it to anonymise PII against a determined attacker. A
/// stronger guarantee (per-process HMAC) is a future option, not required by ADR-026, which mandates
/// only a "hashed / opaque token".
/// </para>
/// <para>
/// This type is stateless (pure functions), consistent with the CONSTITUTION "no static mutable
/// state" rule, and <c>internal</c> like its siblings (<see cref="OrderingKeyResolver"/>,
/// <see cref="OrderedDispatchLaneDepth"/>) — it adds no public API surface.
/// </para>
/// </remarks>
internal static class OrderingKeyDiagnostics
{
    /// <summary>
    /// The constant placeholder rendered in place of an ordering selector in any diagnostic
    /// message. The post-evaluation selector value is NEVER rendered (S1).
    /// </summary>
    internal const string SelectorPlaceholder = "<selector>";

    /// <summary>
    /// Number of leading SHA-256 bytes retained for the opaque token (64 bits → 16 hex chars).
    /// 64 bits keeps collisions negligible for log-correlation volumes while staying short and
    /// readable; a collision only yields ambiguous log attribution (lane mapping keys on the real
    /// value via <see cref="BareWire.Pipeline.GuidHelper.ParseOrHash"/>, never on this token),
    /// never mis-processing.
    /// </summary>
    private const int TokenBytes = 8;

    /// <summary>
    /// Produces a stable, opaque correlation token for an ordering key (S2). The token is a
    /// truncated, lower-/upper-case hex SHA-256 digest of the UTF-8 bytes of <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The raw ordering key. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// A fixed-length (<c>16</c> hex characters) opaque token. The same key always maps to the same
    /// token (SHA-256 is deterministic); distinct keys map to distinct tokens with overwhelming
    /// probability. The token NEVER contains the raw key value.
    /// </returns>
    /// <remarks>
    /// Use exclusively for log correlation (e.g. R8.12 gap-log when parking a poison head). Do NOT
    /// use as a metric dimension (per-key cardinality is forbidden — S2) and do NOT treat as a
    /// cryptographic anonymizer for low-entropy PII (see the type remarks).
    /// </remarks>
    internal static string ToOpaqueToken(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int byteCount = Encoding.UTF8.GetByteCount(key);
        byte[]? rented = byteCount > 256 ? new byte[byteCount] : null;
        Span<byte> utf8 = rented ?? stackalloc byte[256];
        int written = Encoding.UTF8.GetBytes(key, utf8);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8[..written], hash);

        return Convert.ToHexString(hash[..TokenBytes]);
    }

    /// <summary>
    /// Builds a fail-fast ordering-configuration error WITHOUT ever embedding a key value (S1).
    /// The returned exception always has <see cref="BareWireConfigurationException.OptionValue"/>
    /// set to <see langword="null"/>; the diagnostic context is composed exclusively from safe
    /// placeholders (option name, endpoint name, header name, and the constant selector placeholder).
    /// </summary>
    /// <param name="optionName">The misconfigured ordering option name (safe — configuration, not data).</param>
    /// <param name="endpointName">The endpoint the ordering option is bound to (safe — configuration).</param>
    /// <param name="headerName">
    /// The configured ordering header name, or <see langword="null"/>. Safe to surface — it is an
    /// operator-supplied header name, NOT a message value.
    /// </param>
    /// <param name="usesSelector">
    /// <see langword="true"/> when an ordering selector is configured; the message renders the
    /// constant <see cref="SelectorPlaceholder"/>, never the evaluated value.
    /// </param>
    /// <param name="expected">A safe, key-free description of the expected configuration.</param>
    /// <returns>
    /// A <see cref="BareWireConfigurationException"/> whose <c>OptionValue</c> is <see langword="null"/>
    /// and whose message contains no ordering-key value.
    /// </returns>
    internal static BareWireConfigurationException OrderingConfigError(
        string optionName,
        string endpointName,
        string? headerName,
        bool usesSelector,
        string expected)
    {
        // Compose a key-free context from safe placeholders only.
        var keySource = headerName is not null
            ? $"header '{headerName}'"
            : usesSelector
                ? SelectorPlaceholder
                : "correlation-id";

        var safeExpected = $"{expected} (endpoint '{endpointName}', key source: {keySource})";

        // S1 HARD GUARANTEE: optionValue is ALWAYS null — the key value can never reach
        // BareWireConfigurationException.OptionValue or its .Message via this builder.
        return new BareWireConfigurationException(
            optionName,
            optionValue: null,
            expectedValue: safeExpected);
    }
}
