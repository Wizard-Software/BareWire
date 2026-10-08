using AwesomeAssertions;
using BareWire.Outbox.EntityFramework.Internal;
using Xunit;

namespace BareWire.UnitTests.Outbox;

public sealed class AmbientTransactionSupportTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", false)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", true)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", true)]
    [InlineData("Some.Unknown.Provider", true)]
    [InlineData(null, true)]
    // Empty name is unknown -> ambient transaction (fail-closed: EF treats the warning as an error).
    [InlineData("", true)]
    // Comparison is ordinal: a differently-cased name is an unknown provider -> ambient transaction.
    [InlineData("microsoft.entityframeworkcore.sqlite", true)]
    [InlineData("MICROSOFT.ENTITYFRAMEWORKCORE.SQLITE", true)]
    public void IsSupported_ForProviderName_ReturnsExpected(string? providerName, bool expected)
    {
        AmbientTransactionSupport.IsSupported(providerName).Should().Be(expected);
    }
}
