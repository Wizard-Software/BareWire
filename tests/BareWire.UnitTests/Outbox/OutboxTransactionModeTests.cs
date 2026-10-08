using AwesomeAssertions;
using BareWire.Outbox.EntityFramework.Internal;
using Xunit;

namespace BareWire.UnitTests.Outbox;

public sealed class OutboxTransactionModeTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", false)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", true)]
    [InlineData(null, true)]
    public void UseAmbientTransaction_FromProviderName_MatchesClassifier(string? providerName, bool expected)
    {
        var mode = new OutboxTransactionMode(() => providerName);

        mode.UseAmbientTransaction.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UseAmbientTransaction_WhenForced_ReturnsForcedValueWithoutDetection(bool forced)
    {
        var mode = new OutboxTransactionMode(forced);

        mode.UseAmbientTransaction.Should().Be(forced);
    }

    [Fact]
    public void UseAmbientTransaction_ReadMultipleTimes_ReadsProviderNameOnce()
    {
        int calls = 0;
        var mode = new OutboxTransactionMode(() =>
        {
            Interlocked.Increment(ref calls);
            return "Microsoft.EntityFrameworkCore.Sqlite";
        });

        for (int i = 0; i < 5; i++)
        {
            mode.UseAmbientTransaction.Should().BeFalse();
        }

        calls.Should().Be(1);
    }

    [Fact]
    public async Task UseAmbientTransaction_ReadConcurrently_ReturnsConsistentValue()
    {
        var mode = new OutboxTransactionMode(() => "Microsoft.EntityFrameworkCore.Sqlite");

        bool[] results = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(() => mode.UseAmbientTransaction)));

        results.Should().OnlyContain(r => r == false);
    }

    [Fact]
    public void Constructor_NullProviderNameFactory_Throws()
    {
        Action act = () => _ = new OutboxTransactionMode((Func<string?>)null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
