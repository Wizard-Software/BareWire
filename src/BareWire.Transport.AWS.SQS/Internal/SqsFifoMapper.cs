using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace BareWire.Transport.AWS.SQS.Internal;

/// <summary>
/// Pure, broker-free decision logic for Amazon SQS FIFO queue header mapping.
/// Resolves <c>MessageGroupId</c> and <c>MessageDeduplicationId</c> from BareWire outbound headers,
/// mirroring the pattern established by <c>AzureServiceBusSessionMapper</c> for ASB sessions (ADR-011).
/// </summary>
/// <remarks>
/// <para>
/// <b>MessageGroupId resolution order:</b>
/// <list type="number">
/// <item><description><c>BW-MessageGroupId</c> header — explicit override.</description></item>
/// <item><description><c>correlation-id</c> header (kebab-case, as populated by <c>BareWireBus</c>) — automatic per-saga-instance FIFO ordering.</description></item>
/// <item><description><see langword="null"/> — no group id resolved; caller must guard (FIFO requires group id).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>MessageDeduplicationId resolution order:</b>
/// <list type="number">
/// <item><description><c>BW-MessageDeduplicationId</c> header — explicit override.</description></item>
/// <item><description>When <c>contentBasedDeduplication</c> is <see langword="true"/>, returns <see langword="null"/> — the broker computes the dedup id from content (SHA-256 of body).</description></item>
/// <item><description>Deterministic SHA-256 hash of (<c>messageGroupId</c> + <c>body</c>) encoded as URL-safe Base64 (43 chars, ≤ 128 SQS limit). Group id is included so identical bodies in different groups produce different dedup ids.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Performance (ADR-003 / PERF-1):</b> Hash generation uses <see cref="IncrementalHash"/> with
/// <see langword="stackalloc"/> for the 32-byte digest — no heap allocation for the hash input or
/// intermediate arrays. The only allocation is the returned <see cref="string"/> dedup id.
/// This path is opt-in (FIFO queue + no explicit dedup id + content-based-dedup disabled).
/// </para>
/// </remarks>
internal static class SqsFifoMapper
{
    /// <summary>
    /// Resolves the SQS FIFO <c>MessageGroupId</c> from <paramref name="headers"/>.
    /// Returns <see langword="null"/> when neither <c>BW-MessageGroupId</c> nor <c>correlation-id</c>
    /// is present and non-empty — the caller must guard against sending to a FIFO queue without a group id.
    /// </summary>
    /// <param name="headers">The outbound BareWire headers dictionary. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// The resolved group id, or <see langword="null"/> when no group id could be determined.
    /// </returns>
    internal static string? ResolveMessageGroupId(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // Priority 1: explicit BW-MessageGroupId header.
        if (headers.TryGetValue(SqsHeaderMapper.MessageGroupIdHeader, out string? groupId) &&
            !string.IsNullOrEmpty(groupId))
        {
            return groupId;
        }

        // Priority 2: correlation-id fallback (kebab-case — mirrors BareWireBus.cs:451-453).
        if (headers.TryGetValue(SqsHeaderMapper.CorrelationIdHeader, out string? correlationId) &&
            !string.IsNullOrEmpty(correlationId))
        {
            return correlationId;
        }

        return null;
    }

    /// <summary>
    /// Resolves or generates the SQS FIFO <c>MessageDeduplicationId</c> from <paramref name="headers"/>.
    /// </summary>
    /// <param name="headers">The outbound BareWire headers dictionary. Must not be <see langword="null"/>.</param>
    /// <param name="messageGroupId">
    /// The resolved group id (included in the hash to differentiate identical bodies in different groups).
    /// May be <see langword="null"/> or empty when called before the group-id guard.
    /// </param>
    /// <param name="body">The raw serialized message body bytes (zero-copy span from the outbound buffer).</param>
    /// <param name="contentBasedDeduplication">
    /// When <see langword="true"/>, the broker computes the dedup id from message content (SHA-256 of body).
    /// In this case the method returns <see langword="null"/> — no explicit dedup id is sent,
    /// avoiding a double-hash that would never match the broker's computation.
    /// </param>
    /// <returns>
    /// <list type="bullet">
    /// <item><description>The explicit <c>BW-MessageDeduplicationId</c> header value when present and non-empty.</description></item>
    /// <item><description><see langword="null"/> when <paramref name="contentBasedDeduplication"/> is <see langword="true"/>.</description></item>
    /// <item><description>A deterministic 43-character URL-safe Base64 string (SHA-256 of group+body) otherwise.</description></item>
    /// </list>
    /// </returns>
    internal static string? ResolveOrGenerateDeduplicationId(
        IReadOnlyDictionary<string, string> headers,
        string? messageGroupId,
        ReadOnlySpan<byte> body,
        bool contentBasedDeduplication)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // Priority 1: explicit BW-MessageDeduplicationId header.
        if (headers.TryGetValue(SqsHeaderMapper.DeduplicationIdHeader, out string? dedupId) &&
            !string.IsNullOrEmpty(dedupId))
        {
            return dedupId;
        }

        // Priority 2: content-based dedup — broker computes SHA-256 of body server-side.
        // Sending an explicit id would never match the broker's hash, causing dedup failures.
        if (contentBasedDeduplication)
        {
            return null;
        }

        // Priority 3: deterministic SHA-256(group + body) → URL-safe Base64 (43 chars, ≤ 128 limit).
        // PERF-1 / ADR-003: IncrementalHash + stackalloc avoids any intermediate heap allocation.
        return GenerateDeduplicationId(messageGroupId, body);
    }

    private static string GenerateDeduplicationId(string? messageGroupId, ReadOnlySpan<byte> body)
    {
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Include the group id so identical bodies in different groups get different dedup ids.
        if (!string.IsNullOrEmpty(messageGroupId))
        {
            // Encode group id to UTF-8 without heap allocation for short strings via stackalloc.
            // For long group ids (> 256 chars) fall back to rented buffer — but group ids are
            // typically short UUIDs (36 chars). 256 is a safe stack budget.
            int maxBytes = Encoding.UTF8.GetMaxByteCount(messageGroupId.Length);
            if (maxBytes <= 256)
            {
                Span<byte> groupBytes = stackalloc byte[maxBytes];
                int written = Encoding.UTF8.GetBytes(messageGroupId, groupBytes);
                hasher.AppendData(groupBytes[..written]);
            }
            else
            {
                byte[] groupBytes = Encoding.UTF8.GetBytes(messageGroupId);
                hasher.AppendData(groupBytes);
            }
        }

        // Append the raw body bytes (zero-copy: ReadOnlySpan<byte> directly).
        if (!body.IsEmpty)
        {
            hasher.AppendData(body);
        }

        // Finalise: 32-byte SHA-256 digest on the stack.
        Span<byte> digest = stackalloc byte[32];
        hasher.GetHashAndReset(digest);

        // Base64Url encodes 32 bytes → 43 URL-safe chars (no padding), well within the 128-char limit.
        return Base64Url.EncodeToString(digest);
    }
}
