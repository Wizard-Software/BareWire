using BareWire.Abstractions.Outbox;

namespace BareWire.Outbox.EntityFramework.Internal;

// Internal capability of a built-in dialect: claims due retries — rows with a non-null LockedAt older
// than staleCutoff (deferred nacks and abandoned claims of a dead instance) — in due-time order, so a
// large cohort of permanently rejected rows with low ids cannot starve younger retries. The statement
// must be an UPDATE whose affected-row count is the number of rows claimed for instanceId.
internal interface IDueOrderedRetryClaimSql
{
    FormattableString GetDueOrderedRetryClaimSql(
        string instanceId,
        DateTimeOffset now,
        DateTimeOffset staleCutoff,
        int batchSize,
        OrderingMode orderingMode);
}
