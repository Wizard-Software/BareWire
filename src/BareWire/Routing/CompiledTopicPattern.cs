namespace BareWire.Routing;

/// <summary>
/// Classifies a single segment of a compiled topic pattern.
/// </summary>
internal enum TopicSegmentKind : byte
{
    /// <summary>An exact word that must match the corresponding routing-key word verbatim.</summary>
    Literal,

    /// <summary>The <c>*</c> wildcard — matches exactly one word.</summary>
    SingleWord,

    /// <summary>The <c>#</c> wildcard — matches zero or more words.</summary>
    ZeroOrMore,
}

/// <summary>
/// An immutable, pre-built representation of an AMQP topic pattern.
/// </summary>
/// <remarks>
/// <para>
/// Built once at Build()-time by <see cref="TopicPatternMatcher.Compile"/>; never re-parsed
/// per delivery. Holds two parallel arrays indexed by segment position:
/// <list type="bullet">
///   <item><description><see cref="Kinds"/> — the kind (<see cref="TopicSegmentKind"/>) of each segment.</description></item>
///   <item><description><see cref="Literals"/> — meaningful only when <c>Kinds[i] == Literal</c>; otherwise <see cref="string.Empty"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// Specificity metric fields (<see cref="IsExact"/>, <see cref="LiteralWordCount"/>,
/// <see cref="HashCount"/>, <see cref="StarCount"/>, <see cref="LiteralPrefixLength"/>) are
/// computed once in the constructor from the <c>kinds</c> array (Build-time, zero
/// per-delivery cost). They power the D5 comparator
/// (<see cref="ITopicMatcher.CompareSpecificity"/>) without re-parsing the pattern at dispatch time.
/// </para>
/// <para>
/// Word semantics — empty string is zero words; non-empty strings split on <c>.</c> keep empty
/// segments as literal empty words. See <see cref="ITopicMatcher"/> for the full contract.
/// </para>
/// </remarks>
internal readonly struct CompiledTopicPattern
{
    /// <summary>Segment kinds, one entry per pattern word (parallel with <see cref="Literals"/>).</summary>
    internal readonly TopicSegmentKind[] Kinds;

    /// <summary>
    /// Literal values, one entry per segment. Only meaningful when the corresponding
    /// <see cref="Kinds"/> entry is <see cref="TopicSegmentKind.Literal"/>; otherwise <see cref="string.Empty"/>.
    /// </summary>
    internal readonly string[] Literals;

    /// <summary>The original pattern string, retained for diagnostics.</summary>
    internal readonly string Pattern;

    /// <summary>
    /// <see langword="true"/> when the pattern contains no wildcards at all
    /// (<see cref="HashCount"/> == 0 &amp;&amp; <see cref="StarCount"/> == 0).
    /// An exact pattern is always more specific than any pattern with wildcards (D5 criterion K1).
    /// </summary>
    internal readonly bool IsExact;

    /// <summary>
    /// Number of <see cref="TopicSegmentKind.Literal"/> segments in the pattern.
    /// More literal words means higher specificity (D5 criterion K2).
    /// </summary>
    internal readonly int LiteralWordCount;

    /// <summary>
    /// Number of <c>#</c> (<see cref="TopicSegmentKind.ZeroOrMore"/>) wildcards.
    /// Fewer <c>#</c> wildcards means higher specificity (D5 criterion K3a).
    /// </summary>
    internal readonly int HashCount;

    /// <summary>
    /// Number of <c>*</c> (<see cref="TopicSegmentKind.SingleWord"/>) wildcards.
    /// Fewer <c>*</c> wildcards means higher specificity (D5 criterion K3b).
    /// </summary>
    internal readonly int StarCount;

    /// <summary>
    /// Number of consecutive leading <see cref="TopicSegmentKind.Literal"/> segments before the
    /// first wildcard. A longer literal prefix means higher specificity (D5 criterion K4).
    /// For patterns with no wildcards (<see cref="IsExact"/> == <see langword="true"/>) this equals
    /// <see cref="SegmentCount"/>.
    /// </summary>
    internal readonly int LiteralPrefixLength;

    /// <summary>Number of segments in the compiled pattern.</summary>
    internal int SegmentCount => Kinds.Length;

    /// <summary>
    /// Initializes a new <see cref="CompiledTopicPattern"/> and pre-computes all specificity
    /// metric fields in a single pass over <paramref name="kinds"/>. Only
    /// <see cref="TopicPatternMatcher"/> should call this constructor.
    /// </summary>
    internal CompiledTopicPattern(TopicSegmentKind[] kinds, string[] literals, string pattern)
    {
        Kinds = kinds;
        Literals = literals;
        Pattern = pattern;

        // Compute specificity metric fields in one Build-time pass — zero per-delivery cost.
        int hashCount = 0;
        int starCount = 0;
        int literalWordCount = 0;
        int literalPrefixLength = 0;
        bool firstWildcardSeen = false;

        for (int i = 0; i < kinds.Length; i++)
        {
            switch (kinds[i])
            {
                case TopicSegmentKind.Literal:
                    literalWordCount++;
                    if (!firstWildcardSeen)
                    {
                        literalPrefixLength++;
                    }
                    break;

                case TopicSegmentKind.ZeroOrMore:
                    hashCount++;
                    firstWildcardSeen = true;
                    break;

                case TopicSegmentKind.SingleWord:
                    starCount++;
                    firstWildcardSeen = true;
                    break;
            }
        }

        HashCount = hashCount;
        StarCount = starCount;
        LiteralWordCount = literalWordCount;
        LiteralPrefixLength = literalPrefixLength;
        IsExact = hashCount == 0 && starCount == 0;
    }
}
