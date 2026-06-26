using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Routing;

namespace BareWire.UnitTests.Core.Routing;

/// <summary>
/// Unit tests for <see cref="TopicPatternMatcher"/>: AMQP topic semantics, edge cases, and
/// adversarial-complexity proofs.
/// </summary>
public sealed class TopicPatternMatcherTests
{
    private readonly TopicPatternMatcher _matcher = new();

    // ── Helper ────────────────────────────────────────────────────────────────

    private bool Match(string pattern, string routingKey)
    {
        CompiledTopicPattern compiled = _matcher.Compile(pattern);
        return _matcher.IsMatch(in compiled, routingKey.AsSpan());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Compile — segment classification
    // ─────────────────────────────────────────────────────────────────────────

    // TopicSegmentKind is internal; use byte[] in [InlineData] to work around the
    // C# rule that a public method cannot have internal parameter types.
    // Cast to TopicSegmentKind inside the method body (enum : byte, safe cast).
    [Theory]
    [InlineData("a.#",  new[] { "a", "#"     }, new byte[] { (byte)TopicSegmentKind.Literal, (byte)TopicSegmentKind.ZeroOrMore })]
    [InlineData("*",    new[] { "*"           }, new byte[] { (byte)TopicSegmentKind.SingleWord })]
    [InlineData("a..b", new[] { "a", "", "b" }, new byte[] { (byte)TopicSegmentKind.Literal, (byte)TopicSegmentKind.Literal, (byte)TopicSegmentKind.Literal })]
    public void Compile_SplitsPatternIntoSegments(
        string pattern,
        string[] expectedLiterals,
        byte[] expectedKindBytes)
    {
        // Act
        CompiledTopicPattern compiled = _matcher.Compile(pattern);

        // Assert
        compiled.SegmentCount.Should().Be(expectedKindBytes.Length);

        for (int i = 0; i < expectedKindBytes.Length; i++)
        {
            var expectedKind = (TopicSegmentKind)expectedKindBytes[i];
            compiled.Kinds[i].Should().Be(expectedKind);

            if (expectedKind == TopicSegmentKind.Literal)
            {
                compiled.Literals[i].Should().Be(expectedLiterals[i]);
            }
        }
    }

    [Fact]
    public void Compile_EmptyPattern_ReturnsZeroSegments()
    {
        // Act
        CompiledTopicPattern compiled = _matcher.Compile(string.Empty);

        // Assert
        compiled.SegmentCount.Should().Be(0);
        compiled.Pattern.Should().Be(string.Empty);
    }

    [Fact]
    public void Compile_NullPattern_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => _matcher.Compile(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsMatch — canonical RabbitMQ examples
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("*.orange.*",  "quick.orange.rabbit",     true,  "star-orange-star matches three-word key with orange in middle")]
    [InlineData("*.orange.*",  "quick.orange.new.rabbit", false, "star-orange-star does not match four-word key")]
    [InlineData("lazy.#",      "lazy.brown.fox",          true,  "hash absorbs remaining words after literal")]
    [InlineData("lazy.#",      "lazy",                    true,  "hash absorbs zero words when nothing follows")]
    [InlineData("#",           "quick.orange.rabbit",     true,  "bare hash matches any routing key")]
    [InlineData("#",           "a",                       true,  "bare hash matches single-word key")]
    [InlineData("#",           "",                        true,  "bare hash matches empty key (zero words)")]
    public void IsMatch_CanonicalRabbitMqExamples(
        string pattern,
        string routingKey,
        bool expectedMatch,
        string because)
    {
        Match(pattern, routingKey).Should().Be(expectedMatch, because);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsMatch — * = exactly one word
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("*", "a",   true,  "star matches single word")]
    [InlineData("*", "a.b", false, "star does not match two words")]
    [InlineData("*", "",    false, "star does not match empty key (zero words)")]
    public void IsMatch_StarWildcard_ExactlyOneWord(string pattern, string routingKey, bool expectedMatch, string because)
    {
        Match(pattern, routingKey).Should().Be(expectedMatch, because);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsMatch — exact literal patterns
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a.b.c", "a.b.c",   true,  "exact key matches exact pattern")]
    [InlineData("a.b.c", "a.b",     false, "shorter key does not match")]
    [InlineData("a.b.c", "a.b.c.d", false, "longer key does not match")]
    public void IsMatch_ExactPattern_MustMatchWordForWord(string pattern, string routingKey, bool expectedMatch, string because)
    {
        Match(pattern, routingKey).Should().Be(expectedMatch, because);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsMatch — # absorbing zero words (empty-segment / trailing)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a.#", "a",   true,  "hash after literal absorbs zero words")]
    [InlineData("#.b", "b",   true,  "hash before literal absorbs zero words")]
    [InlineData("#",   "",    true,  "bare hash absorbs zero words (empty key)")]
    public void IsMatch_HashZeroOrMoreWords_AbsorbsZeroWords(string pattern, string routingKey, bool expectedMatch, string because)
    {
        Match(pattern, routingKey).Should().Be(expectedMatch, because);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsMatch — empty segments (literal empty word from consecutive dots)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a..b", "a..b", true,  "double-dot pattern matches double-dot key exactly")]
    [InlineData("a..b", "a.b",  false, "double-dot pattern does not match single-dot key")]
    [InlineData("a.",   "a.",   true,  "trailing-dot pattern matches trailing-dot key")]
    [InlineData("a.",   "a",    false, "trailing-dot pattern does not match key without trailing dot")]
    public void IsMatch_EmptySegments_LiteralEmptyWord(string pattern, string routingKey, bool expectedMatch, string because)
    {
        Match(pattern, routingKey).Should().Be(expectedMatch, because);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsMatch — leading / trailing dots
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".a", ".a", true,  "leading-dot pattern matches leading-dot key")]
    [InlineData("a.", "a.", true,  "trailing-dot pattern matches trailing-dot key (redundant alias)")]
    public void IsMatch_LeadingTrailingDots_MatchedLiterally(string pattern, string routingKey, bool expectedMatch, string because)
    {
        Match(pattern, routingKey).Should().Be(expectedMatch, because);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsMatch — empty pattern
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "",  true,  "empty pattern matches empty key")]
    [InlineData("", "a", false, "empty pattern does not match non-empty key")]
    public void IsMatch_EmptyPattern_MatchesOnlyEmptyKey(string pattern, string routingKey, bool expectedMatch, string because)
    {
        Match(pattern, routingKey).Should().Be(expectedMatch, because);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsMatch — multiple # wildcards
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#.#",     "a.b.c",       true,  "two hashes can together absorb three words")]
    [InlineData("a.#.b.#", "a.x.y.b.z",  true,  "interleaved hashes absorb middle and trailing words")]
    [InlineData("#.#",     "",            true,  "two hashes match empty key (both absorb zero words)")]
    public void IsMatch_MultipleHashWildcards_MatchCorrectly(string pattern, string routingKey, bool expectedMatch, string because)
    {
        Match(pattern, routingKey).Should().Be(expectedMatch, because);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ADVERSARIAL COMPLEXITY — proves O(n*m) iterative DP, not exponential backtracking
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsMatch_AdversarialMultiHashVsLongKey_CompletesLinearlyAndReturnsNoMatch()
    {
        // Arrange: pattern with 10 # followed by a literal "x".
        // A recursive backtracker would be exponential on this; the DP is O(n*m).
        const string pattern = "#.#.#.#.#.#.#.#.#.#.x";

        // Routing key: 64 words "w" joined by dots = ~191 chars, well within AMQP 255-byte limit.
        // No trailing "x" → forces full DP traversal with a no-match result.
        string routingKey = string.Join('.', Enumerable.Repeat("w", 64));

        CompiledTopicPattern compiled = _matcher.Compile(pattern);

        var sw = Stopwatch.StartNew();

        // Act
        bool result = _matcher.IsMatch(in compiled, routingKey.AsSpan());

        sw.Stop();

        // Assert correctness
        result.Should().BeFalse("the routing key has no word 'x' so the pattern cannot match");

        // Assert linear time: well under 1 second even on slow CI; exponential backtracking
        // would take seconds or minutes for this input.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "iterative DP completes in microseconds; exponential backtracking would not");
    }
}
