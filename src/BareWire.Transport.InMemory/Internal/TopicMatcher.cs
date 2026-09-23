namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Allocation-free AMQP topic pattern matcher: matches a dot-separated routing key against a
/// dot-separated binding pattern where <c>*</c> matches exactly one word and <c>#</c> matches zero or
/// more words.
/// </summary>
/// <remarks>
/// <para>
/// Words are separated by <c>.</c>; an empty pattern or key has zero words (matching the RabbitMQ
/// broker), while a non-empty span with a leading, trailing, or repeated <c>.</c> produces one or more
/// empty words (for example <c>"a."</c> is the two words <c>"a"</c> and <c>""</c>).
/// </para>
/// <para>
/// The algorithm is the classic wildcard-matching technique (as used for <c>*</c>/<c>?</c> glob
/// matching) adapted to operate on whole words instead of characters: two forward-only cursors track
/// the current word of the pattern and the key, and a single remembered backtrack point — the pattern
/// position right after the most recently seen <c>#</c>, together with the key position it is currently
/// trying to grow from — lets the match retry with one more word absorbed by that <c>#</c> when a later
/// token fails to match. Because both cursors only move forward and the backtrack point advances the key
/// by exactly one word per retry, the worst-case cost is O(n·m) in the number of pattern and key words,
/// with no heap allocation (word boundaries are tracked as <see langword="int"/> indices and words are
/// compared as <see cref="ReadOnlySpan{T}"/> slices).
/// </para>
/// </remarks>
internal static class TopicMatcher
{
    private const char WordSeparator = '.';

    /// <summary>
    /// Returns whether <paramref name="routingKey"/> matches the binding <paramref name="pattern"/>,
    /// using AMQP topic-exchange wildcard semantics (<c>*</c> = exactly one word, <c>#</c> = zero or
    /// more words). A literal <c>*</c> or <c>#</c> appearing in <paramref name="routingKey"/> is matched
    /// as an ordinary word — only tokens in <paramref name="pattern"/> are interpreted as wildcards.
    /// </summary>
    internal static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> routingKey)
    {
        int patternPos = StartPosition(pattern);
        int keyPos = StartPosition(routingKey);

        // The single backtrack point: the pattern position right after the last '#' seen, and the key
        // position that '#' is currently trying to grow from (one more word absorbed per retry).
        int backtrackPatternPos = -1;
        int backtrackKeyPos = -1;
        bool hasBacktrack = false;

        while (true)
        {
            int keyPeekPos = keyPos;
            if (!TryGetNextWord(routingKey, ref keyPeekPos, out ReadOnlySpan<char> keyWord))
            {
                // No more key words — the match succeeds only if every remaining pattern token is '#'.
                int patternPeekPos = patternPos;
                while (TryGetNextWord(pattern, ref patternPeekPos, out ReadOnlySpan<char> trailingWord) && IsHash(trailingWord))
                {
                    patternPos = patternPeekPos;
                }

                int finalPeekPos = patternPos;
                return !TryGetNextWord(pattern, ref finalPeekPos, out _);
            }

            int patternPeekPosForToken = patternPos;
            bool hasPatternWord = TryGetNextWord(pattern, ref patternPeekPosForToken, out ReadOnlySpan<char> patternWord);

            if (hasPatternWord && IsHash(patternWord))
            {
                // Try the zero-word match first: consume the '#' but do not advance the key cursor.
                backtrackPatternPos = patternPeekPosForToken;
                backtrackKeyPos = keyPos;
                hasBacktrack = true;
                patternPos = patternPeekPosForToken;
                continue;
            }

            if (hasPatternWord && (IsStar(patternWord) || patternWord.SequenceEqual(keyWord)))
            {
                patternPos = patternPeekPosForToken;
                keyPos = keyPeekPos;
                continue;
            }

            if (hasBacktrack)
            {
                // Grow the most recent '#' match by exactly one more key word and retry from there.
                int growPos = backtrackKeyPos;
                TryGetNextWord(routingKey, ref growPos, out _);
                backtrackKeyPos = growPos;
                keyPos = growPos;
                patternPos = backtrackPatternPos;
                continue;
            }

            return false;
        }
    }

    /// <summary>
    /// Returns the initial word-cursor position for <paramref name="span"/>: <c>0</c> when it has at
    /// least one word, or <c>span.Length + 1</c> (past the "no more words" sentinel) when it is empty —
    /// an empty routing key or pattern has zero words, never a single empty word.
    /// </summary>
    private static int StartPosition(ReadOnlySpan<char> span) => span.Length == 0 ? span.Length + 1 : 0;

    /// <summary>
    /// Extracts the word starting at <paramref name="pos"/> and advances it to the start of the next
    /// word. Returns <see langword="false"/> (word left empty) once <paramref name="pos"/> has moved
    /// past the end — strictly greater than <c>span.Length</c>, not merely equal to it, so a trailing
    /// separator still yields one final empty word before iteration ends.
    /// </summary>
    private static bool TryGetNextWord(ReadOnlySpan<char> span, ref int pos, out ReadOnlySpan<char> word)
    {
        if (pos > span.Length)
        {
            word = default;
            return false;
        }

        ReadOnlySpan<char> remainder = span[pos..];
        int separatorIndex = remainder.IndexOf(WordSeparator);
        if (separatorIndex < 0)
        {
            word = remainder;
            pos = span.Length + 1;
        }
        else
        {
            word = remainder[..separatorIndex];
            pos += separatorIndex + 1;
        }

        return true;
    }

    private static bool IsHash(ReadOnlySpan<char> word) => word.Length == 1 && word[0] == '#';

    private static bool IsStar(ReadOnlySpan<char> word) => word.Length == 1 && word[0] == '*';
}
