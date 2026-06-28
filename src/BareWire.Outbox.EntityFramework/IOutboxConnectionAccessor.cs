using System.Data.Common;

namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// Exposes the database connection that the transactional outbox middleware has pinned for the
/// message currently being consumed on the active asynchronous flow.
/// </summary>
/// <remarks>
/// <para>
/// The transactional outbox middleware opens a single physical connection for the lifetime of a
/// consume operation and enlists it once in the ambient transaction. A consumer can persist its
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
}
