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
    // Compile — specificity metric fields (17.5)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compile_ExactTwoWordPattern_SetsIsExactAndMetricFields()
    {
        // Arrange & Act
        CompiledTopicPattern compiled = _matcher.Compile("a.b");

        // Assert
        compiled.IsExact.Should().BeTrue("a.b contains no wildcards");
        compiled.LiteralWordCount.Should().Be(2, "a.b has two literal segments");
        compiled.HashCount.Should().Be(0, "a.b has no # wildcards");
        compiled.StarCount.Should().Be(0, "a.b has no * wildcards");
        compiled.LiteralPrefixLength.Should().Be(2, "a.b has two leading literals before any wildcard");
    }

    [Fact]
    public void Compile_MixedWildcardPattern_SetsMetricFields()
    {
        // Arrange & Act
        CompiledTopicPattern compiled = _matcher.Compile("a.*.#");

        // Assert
        compiled.IsExact.Should().BeFalse("a.*.# contains wildcards");
        compiled.LiteralWordCount.Should().Be(1, "a.*.# has one literal segment");
        compiled.HashCount.Should().Be(1, "a.*.# has one # wildcard");
        compiled.StarCount.Should().Be(1, "a.*.# has one * wildcard");
        compiled.LiteralPrefixLength.Should().Be(1, "only the first segment 'a' precedes the first wildcard");
    }

    [Fact]
    public void Compile_EmptyPattern_SetsIsExactTrueAndAllCountsZero()
    {
        // Arrange & Act
        CompiledTopicPattern compiled = _matcher.Compile("");

        // Assert — empty pattern is a degenerate exact pattern (matches only "")
        compiled.IsExact.Should().BeTrue("empty pattern has no wildcards");
        compiled.LiteralWordCount.Should().Be(0, "empty pattern has no segments");
        compiled.HashCount.Should().Be(0, "empty pattern has no # wildcards");
        compiled.StarCount.Should().Be(0, "empty pattern has no * wildcards");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CompareSpecificity — D5 ordering (17.5)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Compiles two patterns and returns CompareSpecificity(a, b).</summary>
    private int ComparePatterns(string a, string b)
    {
        CompiledTopicPattern ca = _matcher.Compile(a);
        CompiledTopicPattern cb = _matcher.Compile(b);
        return _matcher.CompareSpecificity(in ca, in cb);
    }

    [Theory]
    [InlineData("a.b",   "a.*",   "K1: exact wins over non-exact")]
    [InlineData("a.b.*", "a.*",   "K2: more literal words wins over fewer")]
    [InlineData("a.*",   "a.*.*", "K3b: fewer * wins when exact/literals/hashes all tie")]
    [InlineData("a.b.#", "a.#.b", "K4: longer literal prefix wins when literals/hashes/stars all tie")]
    public void CompareSpecificity_MoreSpecificFirst_ReturnsPositive(
        string moreSpecific,
        string lessSpecific,
        string because)
    {
        ComparePatterns(moreSpecific, lessSpecific).Should().BeGreaterThan(0, because);
    }

    [Fact]
    public void CompareSpecificity_TransferStarVsTransferHash_StarIsMoreSpecific()
    {
        // Acceptance example from ADR-030 §D5.
        // transfer.*: LWC=1, HashCount=0, StarCount=1, LiteralPrefixLength=1
        // transfer.#: LWC=1, HashCount=1, StarCount=0, LiteralPrefixLength=1
        // K3a fires first: transfer.* has fewer # → wins.
        ComparePatterns("transfer.*", "transfer.#").Should().BeGreaterThan(0,
            "transfer.* has 0 # wildcards, transfer.# has 1 — fewer # wins (K3a)");
    }

    [Theory]
    [InlineData("a.b",        "a.*",        "K1 antisymmetry")]
    [InlineData("a.b.*",      "a.*",        "K2 antisymmetry")]
    [InlineData("transfer.*", "transfer.#", "K3 antisymmetry")]
    public void CompareSpecificity_Antisymmetry_ReversingOperandsFlipsSign(
        string moreSpecific,
        string lessSpecific,
        string because)
    {
        int forward  = ComparePatterns(moreSpecific, lessSpecific);
        int backward = ComparePatterns(lessSpecific,  moreSpecific);

        forward.Should().BeGreaterThan(0,  because + " (forward: more specific first)");
        backward.Should().BeLessThan(0, because + " (backward: less specific first)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SelectMostSpecific — selection and tie-break (17.5)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SelectMostSpecific_EmptySpan_ReturnsNegativeOneAndNoTie()
    {
        // Act
        int result = _matcher.SelectMostSpecific(ReadOnlySpan<CompiledTopicPattern>.Empty, out bool unresolvedTie);

        // Assert
        result.Should().Be(-1, "empty span has no candidates");
        unresolvedTie.Should().BeFalse("no candidates means no tie");
    }

    [Fact]
    public void SelectMostSpecific_ThreeCandidatesWithClearWinner_ReturnsMostSpecificIndex()
    {
        // Arrange: a.b (exact, K1) > a.b.* (2 literals, non-exact) > a.# (1 literal, non-exact)
        var candidates = new[]
        {
            _matcher.Compile("a.#"),    // index 0: least specific
            _matcher.Compile("a.b.*"),  // index 1: middle
            _matcher.Compile("a.b"),    // index 2: most specific (exact)
        };

        // Act
        int result = _matcher.SelectMostSpecific(candidates.AsSpan(), out bool unresolvedTie);

        // Assert
        result.Should().Be(2, "a.b is exact — K1 makes it the most specific candidate");
        unresolvedTie.Should().BeFalse("there is a clear winner");
    }

    [Fact]
    public void SelectMostSpecific_UnresolvableTie_ReturnsFirstRegisteredAndSetsTieFlag()
    {
        // *.a.# and #.a.* have identical D5 metrics:
        //   IsExact=false, LiteralWordCount=1, HashCount=1, StarCount=1, LiteralPrefixLength=0
        // → K1-K4 all tie → unresolvable tie; first-registered (index 0) wins.
        var candidates = new[]
        {
            _matcher.Compile("*.a.#"),  // index 0 — first registered
            _matcher.Compile("#.a.*"),  // index 1 — second registered
        };

        // Act
        int result = _matcher.SelectMostSpecific(candidates.AsSpan(), out bool unresolvedTie);

        // Assert
        result.Should().Be(0, "first-registered wins on an unresolvable tie");
        unresolvedTie.Should().BeTrue("*.a.# and #.a.* have equal D5 metrics on all criteria");
    }

    [Fact]
    public void SelectMostSpecific_ClearWinner_LeavesUnresolvedTieFalse()
    {
        // Arrange
        var candidates = new[]
        {
            _matcher.Compile("a.b"),  // index 0: exact — most specific
            _matcher.Compile("a.*"),  // index 1: non-exact
        };

        // Act
        int result = _matcher.SelectMostSpecific(candidates.AsSpan(), out bool unresolvedTie);

        // Assert
        result.Should().Be(0, "a.b (exact) is more specific than a.*");
        unresolvedTie.Should().BeFalse("there is a clear winner, no tie");
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
