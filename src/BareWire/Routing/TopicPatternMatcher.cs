using System.Buffers;

namespace BareWire.Routing;

/// <summary>
/// Default <see cref="ITopicMatcher"/> implementation using an iterative, O(n*m) streaming DP
/// algorithm (no recursion, no backtracking).
/// </summary>
/// <remarks>
/// <para>
/// <b>Compile (Build-time):</b> scans the pattern string character-by-character for <c>.</c>
/// delimiters, classifying each segment as <see cref="TopicSegmentKind.Literal"/>,
/// <see cref="TopicSegmentKind.SingleWord"/> (<c>*</c>), or
/// <see cref="TopicSegmentKind.ZeroOrMore"/> (<c>#</c>). Allocation is permitted here because
/// <c>Compile</c> is called once at Bus build-time.
/// </para>
/// <para>
/// <b>IsMatch (hot path, per-delivery):</b> uses two rolling boolean DP rows allocated with
/// <c>stackalloc</c> (normal path, zero heap alloc) or rented from <see cref="ArrayPool{T}"/>
/// when <c>SegmentCount + 1 &gt; 256</c> (defensive hardening against pathologically long
/// patterns; AMQP routing keys are bounded to 255 bytes so the stack path is the norm).
/// Routing-key words are enumerated in-place by scanning the <see cref="ReadOnlySpan{T}"/> for
/// <c>.</c> — no <c>string.Split</c>, no per-delivery list.
/// </para>
/// <para>
/// <b>Adversarial safety:</b> the DP transition is strictly O(n*m) where n = segment count and
/// m = word count. Multiple <c>#</c> wildcards that would be exponential under naive recursive
/// backtracking are handled in linear passes. A unit test with 10+ <c>#</c> against a ~255-byte
/// routing key confirms sub-millisecond completion.
/// </para>
/// </remarks>
internal sealed class TopicPatternMatcher : ITopicMatcher
{
    /// <summary>
    /// Maximum number of DP cells (SegmentCount + 1) that may be allocated on the stack.
    /// Patterns exceeding this threshold fall back to <see cref="ArrayPool{T}"/>.
    /// </summary>
    private const int StackAllocCap = 256;

    /// <inheritdoc/>
    public CompiledTopicPattern Compile(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0)
        {
            return new CompiledTopicPattern([], [], pattern);
        }

        // Count dots to pre-size arrays (avoids List<T> growth allocations).
        int segCount = 1;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '.')
            {
                segCount++;
            }
        }

        var kinds = new TopicSegmentKind[segCount];
        var literals = new string[segCount];

        int seg = 0;
        int start = 0;

        for (int i = 0; i <= pattern.Length; i++)
        {
            if (i == pattern.Length || pattern[i] == '.')
            {
                ReadOnlySpan<char> word = pattern.AsSpan(start, i - start);

                (kinds[seg], literals[seg]) = word switch
                {
                    "#" => (TopicSegmentKind.ZeroOrMore, string.Empty),
                    "*" => (TopicSegmentKind.SingleWord, string.Empty),
                    _   => (TopicSegmentKind.Literal, word.ToString()),
                };

                seg++;
                start = i + 1;
            }
        }

        return new CompiledTopicPattern(kinds, literals, pattern);
    }

    /// <inheritdoc/>
    public bool IsMatch(in CompiledTopicPattern pattern, ReadOnlySpan<char> routingKey)
    {
        int n = pattern.SegmentCount;
        int dpSize = n + 1;

        // Use stackalloc for patterns within the cap; fall back to ArrayPool for safety.
        if (dpSize <= StackAllocCap)
        {
            Span<bool> dp  = stackalloc bool[dpSize];
            Span<bool> ndp = stackalloc bool[dpSize];
            return RunDp(in pattern, routingKey, dp, ndp);
        }
        else
        {
            bool[]? dpArr  = null;
            bool[]? ndpArr = null;
            try
            {
                dpArr  = ArrayPool<bool>.Shared.Rent(dpSize);
                ndpArr = ArrayPool<bool>.Shared.Rent(dpSize);

                // ArrayPool may return a larger array; slice to exact size.
                Span<bool> dp  = dpArr.AsSpan(0, dpSize);
                Span<bool> ndp = ndpArr.AsSpan(0, dpSize);
                return RunDp(in pattern, routingKey, dp, ndp);
            }
            finally
            {
                if (dpArr  is not null) ArrayPool<bool>.Shared.Return(dpArr);
                if (ndpArr is not null) ArrayPool<bool>.Shared.Return(ndpArr);
            }
        }
    }

    /// <summary>
    /// Core streaming-DP matching kernel, operating on caller-supplied DP row spans.
    /// </summary>
    /// <remarks>
    /// dp[j] = true when pattern segments [0..j) match the routing-key words consumed so far.
    ///
    /// Initialization (zero words consumed):
    ///   dp[0] = true (empty prefix of pattern matches empty prefix of key)
    ///   dp[j] = dp[j-1] when Kinds[j-1] == ZeroOrMore (# can absorb zero words)
    ///   dp[j] = false  otherwise
    ///
    /// For each word w_i:
    ///   ndp[0] = false (no pattern segment can match i > 0 words using 0 segments)
    ///   ZeroOrMore (#): ndp[j] = dp[j]       -- # absorbs w_i and stays at j
    ///                           || ndp[j-1]   -- # absorbs zero words, pattern moves past j
    ///   SingleWord (*): ndp[j] = dp[j-1]     -- * matches exactly w_i
    ///   Literal:        ndp[j] = dp[j-1] and w_i equals Literals[j-1]
    ///
    /// After all m words: result is dp[n].
    /// </remarks>
    private static bool RunDp(
        in CompiledTopicPattern pattern,
        ReadOnlySpan<char> routingKey,
        Span<bool> dp,
        Span<bool> ndp)
    {
        int n = pattern.SegmentCount;
        TopicSegmentKind[] kinds    = pattern.Kinds;
        string[]           literals = pattern.Literals;

        // ── Initialize row for zero words consumed ────────────────────────────
        dp[0] = true;
        for (int j = 1; j <= n; j++)
        {
            dp[j] = kinds[j - 1] == TopicSegmentKind.ZeroOrMore && dp[j - 1];
        }

        // Empty routing key: m = 0 words, result is the init row.
        if (routingKey.IsEmpty)
        {
            return dp[n];
        }

        // ── Stream words from routingKey ──────────────────────────────────────
        ReadOnlySpan<char> remaining = routingKey;

        while (true)
        {
            // Extract next word (up to the next dot or end of span).
            ReadOnlySpan<char> word;
            int dotIdx = remaining.IndexOf('.');

            bool isLast;
            if (dotIdx < 0)
            {
                word    = remaining;
                isLast  = true;
            }
            else
            {
                word      = remaining[..dotIdx];
                remaining = remaining[(dotIdx + 1)..];
                isLast    = false;
            }

            // ── Compute next DP row ───────────────────────────────────────────
            ndp[0] = false;
            for (int j = 1; j <= n; j++)
            {
                ndp[j] = kinds[j - 1] switch
                {
                    TopicSegmentKind.ZeroOrMore =>
                        dp[j] || ndp[j - 1],

                    TopicSegmentKind.SingleWord =>
                        dp[j - 1],

                    _ => // Literal
                        dp[j - 1] && word.SequenceEqual(literals[j - 1].AsSpan()),
                };
            }

            // Roll: ndp becomes the new dp.
            ndp.CopyTo(dp);

            if (isLast)
            {
                break;
            }
        }

        return dp[n];
    }
}
