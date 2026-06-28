using System.Data.Common;

namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// Default <see cref="IOutboxConnectionAccessor"/> implementation. Reads the connection pinned by
/// <see cref="TransactionalOutboxMiddleware"/> on the current asynchronous flow. Stateless and
/// thread-safe — safe to register as a singleton.
/// </summary>
internal sealed class OutboxConnectionAccessor : IOutboxConnectionAccessor
{
    public DbConnection? Current => TransactionalOutboxMiddleware.CurrentConnection;
}
