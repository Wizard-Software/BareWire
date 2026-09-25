using AwesomeAssertions;
using BareWire.Transport.InMemory.Internal;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class TopicMatcherTests
{
    [Theory]
    [InlineData("orders.created", "orders.created", true)]
    [InlineData("orders.*", "orders.created", true)]
    [InlineData("orders.*", "orders.created.eu", false)]
    [InlineData("orders.*", "orders", false)]
    [InlineData("orders.#", "orders", true)]
    [InlineData("orders.#", "orders.created.eu", true)]
    [InlineData("#", "", true)]
    [InlineData("*", "", false)]
    [InlineData("#.eu", "orders.created.eu", true)]
    [InlineData("#.eu.#", "eu", true)]
    [InlineData("*.*.eu", "orders.eu", false)]
    [InlineData("a.#.b.#.c", "a.x.b.y.z.c", true)]
    [InlineData("a.#.b", "a.b.c", false)]
    [InlineData("orders.created", "orders.Created", false)]
    // Empty-word edge cases (a trailing "." produces a trailing empty word, matching RabbitMQ semantics).
    [InlineData("a.*", "a.", true)]
    [InlineData("*.a", ".a", true)]
    [InlineData("#", ".", true)]
    [InlineData("*", ".", false)]
    [InlineData("*.*", ".", true)]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("a.b", "a.*", false)] // literal '*' in the routing key must not act as a wildcard
    [InlineData("a.*", "a.#", true)] // literal '#' in the routing key must match a literal single-word pattern token
    public void IsMatch_ForPatternAndKey_ReturnsExpected(string pattern, string key, bool expected) =>
        TopicMatcher.IsMatch(pattern, key).Should().Be(expected);

    [Fact]
    public void IsMatch_ManyHashSegmentsWithoutTrailingLiteralWord_ReturnsFalse()
    {
        string pattern = string.Join('.', Enumerable.Repeat("#", 10)) + ".z";
        string key = string.Join('.', Enumerable.Repeat("a", 20));

        TopicMatcher.IsMatch(pattern, key).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_WhenCalledRepeatedly_AllocatesNothing()
    {
        // Warm-up outside the measured loop so JIT/tiering allocations are not counted.
        for (int i = 0; i < 100; i++)
        {
            TopicMatcher.IsMatch("a.#.b.*.c", "a.x.y.b.z.c");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            TopicMatcher.IsMatch("a.#.b.*.c", "a.x.y.b.z.c");
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }
}
