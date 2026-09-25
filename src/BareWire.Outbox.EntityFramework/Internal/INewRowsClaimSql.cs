using BareWire.Abstractions.Outbox;

namespace BareWire.Outbox.EntityFramework.Internal;

// Internal capability of a built-in dialect: claims only never-claimed rows (LockedAt IS NULL) in id
// order with a predicate and ordering the claim index can serve as an ordered range scan. The public
// claim statement combines this class with stale locks through an OR predicate, which the planner
// cannot serve in order from that index. The statement must be an UPDATE whose affected-row count is
// the number of rows claimed for instanceId.
internal interface INewRowsClaimSql
{
    FormattableString GetNewRowsClaimSql(
        string instanceId,
        DateTimeOffset now,
        int batchSize,
        OrderingMode orderingMode);
}
