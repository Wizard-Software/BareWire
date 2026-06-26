namespace BareWire.Routing;

/// <summary>
/// Seam for matching AMQP topic patterns against routing keys and comparing their specificity.
/// </summary>
/// <remarks>
/// <para>
/// Separates pattern compilation (Build-time, allocation allowed) from matching (hot path,
/// zero-alloc). Implementations compile a pattern string exactly once via <see cref="Compile"/>
/// and then test individual delivery routing keys cheaply via <see cref="IsMatch"/>.
/// </para>
/// <para>
/// Specificity comparison (<see cref="CompareSpecificity"/>) and most-specific selection
/// (<see cref="SelectMostSpecific"/>) are also zero-alloc hot-path operations that operate
/// exclusively on the pre-computed integer fields of <see cref="CompiledTopicPattern"/>
/// (D5 metric — see <see cref="CompareSpecificity"/> for the ordering contract).
/// </para>
/// <para>
/// Word semantics (AMQP / RabbitMQ):
/// <list type="bullet">
///   <item><description>
///     An empty string <c>""</c> represents <b>zero words</b>. So <c>#</c> matches <c>""</c>,
///     while <c>*</c> does not, and an empty pattern matches only <c>""</c>.
///   </description></item>
///   <item><description>
///     Non-empty strings split on <c>.</c> yield (dot-count + 1) words; some words may be the
///     empty string (e.g. <c>"a."</c> → <c>["a", ""]</c>). The same split rule applies to both
///     the pattern and the routing key.
///   </description></item>
///   <item><description>
///     <c>*</c> — matches exactly one word (any content, including empty).
///   </description></item>
///   <item><description>
///     <c>#</c> — matches zero or more words.
///   </description></item>
///   <item><description>
///     Literals — matched word-for-word, including the empty word.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
internal interface ITopicMatcher
{
    /// <summary>
    /// Compiles a topic pattern string into a pre-built representation.
    /// Called once at Build()-time; allocation is permitted here.
    /// </summary>
    /// <param name="pattern">The AMQP topic pattern (e.g. <c>"lazy.#"</c>, <c>"*.orange.*"</c>).</param>
    /// <returns>A <see cref="CompiledTopicPattern"/> ready for repeated zero-alloc matching.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pattern"/> is <see langword="null"/>.</exception>
    CompiledTopicPattern Compile(string pattern);

    /// <summary>
    /// Tests whether a pre-compiled topic pattern matches the given routing key.
    /// This is the hot (per-delivery) path and MUST NOT allocate heap memory.
    /// </summary>
    /// <param name="pattern">The pre-compiled pattern produced by <see cref="Compile"/>.</param>
    /// <param name="routingKey">The delivery routing key as a character span.</param>
    /// <returns><see langword="true"/> if the routing key matches the pattern; otherwise <see langword="false"/>.</returns>
    bool IsMatch(in CompiledTopicPattern pattern, ReadOnlySpan<char> routingKey);

    /// <summary>
    /// Compares two pre-compiled patterns using the D5 specificity ordering (to the first
    /// difference, zero-alloc):
    /// <list type="number">
    ///   <item><description>K1 — exact pattern (no wildcards) beats any pattern with wildcards.</description></item>
    ///   <item><description>K2 — more literal words is more specific.</description></item>
    ///   <item><description>K3a — fewer <c>#</c> wildcards is more specific.</description></item>
    ///   <item><description>K3b — fewer <c>*</c> wildcards is more specific.</description></item>
    ///   <item><description>K4 — longer leading literal prefix is more specific.</description></item>
    ///   <item><description>K5 — all equal → unresolvable tie, returns <c>0</c>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="a">First pre-compiled pattern.</param>
    /// <param name="b">Second pre-compiled pattern.</param>
    /// <returns>
    /// A positive value when <paramref name="a"/> is more specific than <paramref name="b"/>;
    /// a negative value when <paramref name="b"/> is more specific; <c>0</c> when the ordering
    /// cannot be resolved (unresolvable tie on all D5 criteria).
    /// </returns>
    int CompareSpecificity(in CompiledTopicPattern a, in CompiledTopicPattern b);

    /// <summary>
    /// Selects the index of the most specific pattern among <paramref name="candidates"/> using
    /// <see cref="CompareSpecificity"/>. On an unresolvable tie between the current winner and a
    /// later candidate the first-registered index is preserved and <paramref name="unresolvedTie"/>
    /// is set to <see langword="true"/>. Returns <c>-1</c> for an empty span.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Precondition:</strong> <paramref name="candidates"/> must be supplied in
    /// <em>registration order</em>. The deterministic first-registered tie-break depends on
    /// the caller maintaining this ordering (cross-task contract for the 17.7 dispatch layer).
    /// </para>
    /// <para>
    /// A strict new winner resets <paramref name="unresolvedTie"/> to <see langword="false"/>
    /// (only the current best participates in tie detection; a prior tie is irrelevant once
    /// a strictly better pattern is found).
    /// </para>
    /// </remarks>
    /// <param name="candidates">
    /// Span of pre-compiled patterns in registration order. Empty span returns <c>-1</c>.
    /// </param>
    /// <param name="unresolvedTie">
    /// Set to <see langword="true"/> when the returned index ties on all D5 criteria with at
    /// least one other candidate (diagnostic signal for the 17.7 warning emitter);
    /// <see langword="false"/> otherwise.
    /// </param>
    /// <returns>
    /// Index of the most specific candidate (first-registered on tie); <c>-1</c> for empty span.
    /// </returns>
    int SelectMostSpecific(ReadOnlySpan<CompiledTopicPattern> candidates, out bool unresolvedTie);
}
