namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// Classifies EF Core providers by whether their ADO.NET driver can enlist in a
/// <c>System.Transactions</c> ambient transaction.
/// </summary>
internal static class AmbientTransactionSupport
{
    /// <summary>EF Core provider name of <c>Microsoft.EntityFrameworkCore.Sqlite</c>.</summary>
    internal const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// Returns <see langword="false"/> only for providers known NOT to support ambient enlistment (SQLite).
    /// A <see langword="null"/>, empty, differently-cased or otherwise unknown provider name returns
    /// <see langword="true"/> (ordinal comparison), so unrecognised providers keep the ambient
    /// transaction behaviour — fail-closed, because EF Core treats the ambient-transaction warning
    /// as an error by default.
    /// </summary>
    internal static bool IsSupported(string? providerName)
        => !string.Equals(providerName, SqliteProviderName, StringComparison.Ordinal);
}
