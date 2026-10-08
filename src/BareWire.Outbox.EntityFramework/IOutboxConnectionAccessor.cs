using System.Data.Common;

namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// Exposes the database connection that the transactional outbox middleware has pinned for the
/// message currently being consumed on the active asynchronous flow.
/// </summary>
/// <remarks>
/// <para>
/// The transactional outbox middleware opens a single physical connection for the lifetime of a
/// consume operation and binds it to one transaction: either an ambient <c>TransactionScope</c>
/// (providers whose driver can enlist in it, such as Npgsql) or, for providers that cannot (such as
/// SQLite), an explicit local transaction exposed through <see cref="CurrentTransaction"/>. A consumer can persist its
/// own business state through that <em>same</em> connection — instead of opening a second one — so
/// that the business write, the outbox messages, and the inbox processed marker all commit as a
/// single-phase commit. Sharing one connection avoids escalation to a two-phase (prepared) commit,
/// which is both faster and free of the PostgreSQL <c>max_prepared_transactions</c> requirement.
/// </para>
/// <para>
/// Typical usage is to configure a consumer's <c>DbContext</c> to use <see cref="Current"/> when it
/// is non-<see langword="null"/> and fall back to its own connection otherwise (startup schema
/// initialization, HTTP request handlers, or any path that runs outside a consume operation):
/// </para>
/// <code>
/// services.AddDbContext&lt;MyDbContext&gt;((sp, options) =&gt;
/// {
///     DbConnection? shared = sp.GetRequiredService&lt;IOutboxConnectionAccessor&gt;().Current;
///     if (shared is not null)
///         options.UseNpgsql(shared);          // share the outbox connection → single commit
///     else
///         options.UseNpgsql(connectionString); // standalone connection
/// });
/// </code>
/// <para>
/// When <see cref="CurrentTransaction"/> is non-<see langword="null"/> the consumer <c>DbContext</c> must
/// also join it, otherwise its writes would not be part of the consume transaction:
/// </para>
/// <code>
/// IOutboxConnectionAccessor accessor = sp.GetRequiredService&lt;IOutboxConnectionAccessor&gt;();
/// if (accessor.CurrentTransaction is { } transaction)
///     db.Database.UseTransaction(transaction); // join the local consume transaction
/// </code>
/// <para>
/// The transaction is owned by the middleware. The consumer must never call <c>Commit</c>,
/// <c>Rollback</c> or <c>Dispose</c> on it, nor <c>Database.CommitTransactionAsync()</c> after
/// <c>UseTransaction</c>; the middleware commits it together with the outbox messages and the inbox
/// processed marker, and fails the consume operation if the consumer completed it.
/// </para>
/// <para>
/// The accessor is registered as a singleton by <see cref="ServiceCollectionExtensions.AddBareWireOutbox"/>.
/// It is backed by an asynchronous-flow-local value, so <see cref="Current"/> reflects the
/// connection pinned by the outbox middleware on the caller's logical execution context.
/// </para>
/// </remarks>
public interface IOutboxConnectionAccessor
{
    /// <summary>
    /// Gets the open <see cref="DbConnection"/> the transactional outbox middleware has pinned for
    /// the in-flight consume operation on the current asynchronous flow, or <see langword="null"/>
    /// when no outbox consume operation is in progress.
    /// </summary>
    DbConnection? Current { get; }

    /// <summary>
    /// Gets the local <see cref="DbTransaction"/> the transactional outbox middleware opened on
    /// <see cref="Current"/> when the provider cannot enlist in an ambient <c>System.Transactions</c>
    /// transaction (for example SQLite), or <see langword="null"/> when the middleware uses an ambient
    /// <c>TransactionScope</c> or no consume operation is in progress.
    /// </summary>
    /// <remarks>
    /// A consumer <c>DbContext</c> sharing <see cref="Current"/> must call
    /// <c>Database.UseTransaction(CurrentTransaction)</c> when this value is non-<see langword="null"/>
    /// so its writes commit together with the outbox messages. The transaction is owned by the
    /// middleware: the consumer must never <c>Commit</c>, <c>Rollback</c> or <c>Dispose</c> it, nor call
    /// <c>Database.CommitTransactionAsync()</c> after <c>UseTransaction</c>.
    /// </remarks>
    DbTransaction? CurrentTransaction => null;
}
