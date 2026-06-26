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
/// The original <see cref="Pattern"/> string is retained for diagnostics and serves as the anchor
/// for the specificity score that task 17.5 will add (a pre-computed integer, derived in
/// <c>Compile</c>, will accompany or extend this struct without changing the seam).
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

    /// <summary>The original pattern string, kept for diagnostics and the 17.5 specificity score.</summary>
    internal readonly string Pattern;

    /// <summary>Number of segments in the compiled pattern.</summary>
    internal int SegmentCount => Kinds.Length;

    /// <summary>
    /// Initializes a new <see cref="CompiledTopicPattern"/>.
    /// Only <see cref="TopicPatternMatcher"/> should call this constructor.
    /// </summary>
    internal CompiledTopicPattern(TopicSegmentKind[] kinds, string[] literals, string pattern)
    {
        Kinds = kinds;
        Literals = literals;
        Pattern = pattern;
    }
}
