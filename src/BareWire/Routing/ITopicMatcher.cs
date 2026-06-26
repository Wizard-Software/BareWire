namespace BareWire.Routing;

/// <summary>
/// Seam for matching AMQP topic patterns against routing keys.
/// </summary>
/// <remarks>
/// <para>
/// Separates pattern compilation (Build-time, allocation allowed) from matching (hot path,
/// zero-alloc). Implementations compile a pattern string exactly once via <see cref="Compile"/>
/// and then test individual delivery routing keys cheaply via <see cref="IsMatch"/>.
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
/// <para>
/// Future: task 17.5 will extend <see cref="CompiledTopicPattern"/> with a specificity score
/// computed at compile time, and this interface with a comparison method. The <c>Compile</c> /
/// <c>IsMatch</c> split is the anchor for that extension.
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
}
