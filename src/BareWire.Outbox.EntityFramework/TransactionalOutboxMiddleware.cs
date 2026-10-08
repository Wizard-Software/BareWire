using System.Data.Common;
using System.Transactions;
using BareWire.Abstractions.Pipeline;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

using static BareWire.Abstractions.Pipeline.WellKnownItemKeys;

namespace BareWire.Outbox.EntityFramework;

internal sealed partial class TransactionalOutboxMiddleware : IMessageMiddleware
{
    // Header key written by RabbitMqHeaderMapper for the message type discriminator.
    private const string MessageTypeHeader = "BW-MessageType";

    // Metric tag used when the context carries no endpoint name — the dedup key may then fall back to
    // the producer-controlled message-type header, which must never become a metric tag value.
    private const string UnknownEndpointMetricTag = "unknown";

    private static readonly AsyncLocal<OutboxBuffer?> _current = new();

    // The physical connection pinned for the in-flight consume operation, flowed across the
    // consumer's (separate) DI scope via the async execution context. A consumer DbContext can
    // share this exact connection so its business write commits single-phase with the outbox
    // write and the inbox marker — no second connection, no escalation to a two-phase commit.
    private static readonly AsyncLocal<DbConnection?> _currentConnection = new();

    // The LOCAL transaction opened for the in-flight consume operation when the provider cannot enlist
    // in an ambient System.Transactions transaction (for example SQLite). Flowed alongside the pinned
    // connection so a consumer DbContext sharing that connection can join it via UseTransaction.
    // Null while an ambient TransactionScope is used or no consume operation is in progress.
    private static readonly AsyncLocal<DbTransaction?> _currentTransaction = new();

    private readonly OutboxDbContext _dbContext;
    private readonly IOutboxStore _outboxStore;
    private readonly InboxFilter _inboxFilter;
    private readonly ILogger<TransactionalOutboxMiddleware> _logger;
    private readonly OutboxTransactionMode _transactionMode;
    private readonly int _maxBufferedMessages;
    private readonly long _maxBufferedBytes;

    internal static OutboxBuffer? Current => _current.Value;

    internal static DbConnection? CurrentConnection => _currentConnection.Value;

    internal static DbTransaction? CurrentTransaction => _currentTransaction.Value;

    internal TransactionalOutboxMiddleware(
        OutboxDbContext dbContext,
        IOutboxStore outboxStore,
        InboxFilter inboxFilter,
        ILogger<TransactionalOutboxMiddleware> logger,
        OutboxTransactionMode transactionMode,
        OutboxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(outboxStore);
        ArgumentNullException.ThrowIfNull(inboxFilter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(transactionMode);

        _dbContext = dbContext;
        _outboxStore = outboxStore;
        _inboxFilter = inboxFilter;
        _logger = logger;
        _transactionMode = transactionMode;
        OutboxOptions effective = options ?? OutboxOptions.Default;
        _maxBufferedMessages = effective.MaxBufferedMessagesPerConsume;
        _maxBufferedBytes = effective.MaxBufferedBytesPerConsume;
    }

    // Must stay a SYNCHRONOUS method: a TransactionScope created inside an async method would not
    // flow Transaction.Current back to the caller (AsyncLocal semantics).
    private static TransactionScope CreateAmbientScope() =>
        new(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled);

    public async Task InvokeAsync(MessageContext context, NextMiddleware nextMiddleware)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nextMiddleware);

        CancellationToken ct = context.CancellationToken;

        // Derive consumer type from EndpointName when available (unique per queue), fall back to
        // BW-MessageType header, then to type name. Using EndpointName prevents two consumers on
        // different queues sharing the same message type from colliding on the inbox key.
        string consumerType = !string.IsNullOrEmpty(context.EndpointName)
            ? context.EndpointName
            : context.Headers.TryGetValue(MessageTypeHeader, out string? headerValue)
                ? headerValue
                : context.GetType().Name;

        // Open and hold the physical database connection for the entire operation.
        // Keeping one connection open ensures SaveChangesAsync and MarkProcessedAsync
        // (ExecuteUpdateAsync) share ONE physical connection — either enlisted once in the ambient
        // TransactionScope (preventing DTC escalation on Npgsql / non-Windows hosts) or bound to a
        // local transaction on providers that cannot enlist in an ambient one (SQLite).
        await _dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Publish the pinned connection on the async flow so a consumer DbContext (resolved in its
        // own DI scope, but on the same execution context) can share this exact connection. Sharing
        // one connection lets the business write commit single-phase with the outbox messages and the
        // inbox marker — no second connection, hence no escalation to a two-phase (prepared) commit.
        _currentConnection.Value = _dbContext.Database.GetDbConnection();
        try
        {
            // 1. Inbox deduplication check — deliberately OUTSIDE the transaction so the
            //    lock row is committed immediately and visible to other workers even if the
            //    business transaction later rolls back.
            bool lockAcquired = await _inboxFilter
                .TryLockAsync(
                    context.MessageId,
                    consumerType,
                    metricConsumerTag: !string.IsNullOrEmpty(context.EndpointName)
                        ? context.EndpointName
                        : UnknownEndpointMetricTag,
                    ct)
                .ConfigureAwait(false);

            if (!lockAcquired)
            {
                TransactionalOutboxLogMessages.DuplicateMessageSkipped(_logger, context.MessageId);
                context.Items[InboxFiltered] = true;
                return;
            }

            var buffer = new OutboxBuffer(_maxBufferedMessages, _maxBufferedBytes);
            _current.Value = buffer;

            // In-process retries (RetryMiddleware sits inside this middleware) re-run the handler on the
            // same buffer; discard what the failed attempt published so only the successful attempt's
            // messages reach the outbox.
            Action retryCallback = buffer.Clear;
            context.Items[RetryAttemptStarting] = retryCallback;

            // 2. Begin the transaction. Ambient mode: the already-open connection is enlisted once in a
            //    TransactionScope, so no second connection is opened and DTC is never triggered. Local
            //    mode: an explicit transaction is begun on the pinned connection (inside the try below,
            //    so a failure to begin still runs the cleanup). Both SaveChangesAsync and
            //    MarkProcessedAsync use the same connection either way.
            //    The scope is created by a synchronous helper so Transaction.Current stays visible to
            //    this method's continuation (AsyncLocal semantics).
            TransactionScope? scope = _transactionMode.UseAmbientTransaction ? CreateAmbientScope() : null;
            IDbContextTransaction? localTransaction = null;
            DbTransaction? localDbTransaction = null;

            try
            {
                if (scope is null)
                {
                    // ReadUncommitted defers the physical BEGIN until the first write, so the database
                    // write lock is not held while the handler runs (a plain BeginTransactionAsync on
                    // SQLite issues BEGIN IMMEDIATE).
                    localTransaction = await _dbContext.Database
                        .BeginTransactionAsync(System.Data.IsolationLevel.ReadUncommitted, ct)
                        .ConfigureAwait(false);
                    localDbTransaction = localTransaction.GetDbTransaction();
                    _currentTransaction.Value = localDbTransaction;
                }

                // 3. Invoke handler — business logic runs inside the transaction.
                await nextMiddleware(context).ConfigureAwait(false);

                // The local transaction belongs to this middleware. If the consumer completed it
                // (Commit / Rollback / Dispose), the outbox flush and inbox marker below would run
                // outside the business write's transaction and silently break exactly-once.
                if (localDbTransaction is not null && localDbTransaction.Connection is null)
                {
                    throw new InvalidOperationException(
                        "The consume transaction was completed by the consumer. The transaction exposed by " +
                        "IOutboxConnectionAccessor.CurrentTransaction is owned by the transactional outbox " +
                        "middleware; consumers must not commit, roll back or dispose it.");
                }

                // 4. Flush outbox buffer — add OutboxMessage entities to DbContext (no SaveChanges yet).
                //    Seal first: work that outlives the handler (e.g. Task.Run) captured the AsyncLocal
                //    buffer, and must not append after the snapshot below has been taken.
                buffer.Seal();
                if (!buffer.IsEmpty)
                {
                    var messages = buffer.GetMessages();
                    TransactionalOutboxLogMessages.FlushingBuffer(_logger, context.MessageId, messages.Count);
                    await _outboxStore.SaveMessagesAsync(messages, ct).ConfigureAwait(false);
                }

                // 5. Atomically persist business state + outbox messages.
                await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

                // 6. Mark the inbox entry as permanently processed — atomically with the business
                //    state and outbox messages in this same TransactionScope. No window exists
                //    between committing the business state and setting ProcessedAt: either all
                //    three writes commit together via scope.Complete(), or all three roll back.
                await _inboxFilter.MarkProcessedAsync(context.MessageId, consumerType, ct)
                    .ConfigureAwait(false);

                // 7. Commit: business state + outbox messages + processed marker atomically.
                if (localTransaction is not null)
                {
                    await localTransaction.CommitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    scope!.Complete();
                }

                TransactionalOutboxLogMessages.TransactionCompleted(_logger, context.MessageId);
            }
            catch
            {
                // Buffer is discarded; DbContext changes are not saved; the transaction is disposed
                // without being committed — automatic rollback of all three writes.
                int discardCount = buffer.Count;
                TransactionalOutboxLogMessages.DiscardingBuffer(_logger, context.MessageId, discardCount);
                buffer.Clear();
                throw;
            }
            finally
            {
                buffer.Seal();
                _current.Value = null;

                // The retry slot is owned by this middleware; if inner middleware replaced or removed it,
                // retried attempts may have leaked duplicate messages into the buffer.
                if (!context.Items.TryGetValue(RetryAttemptStarting, out object? slot)
                    || !ReferenceEquals(slot, retryCallback))
                {
                    TransactionalOutboxLogMessages.RetryCallbackOverwritten(_logger, context.MessageId);
                }

                context.Items.Remove(RetryAttemptStarting);

                // Stop exposing the local transaction before it is disposed — it must never leak to a
                // subsequent message processed on this asynchronous flow.
                _currentTransaction.Value = null;

                // Dispose (= roll back when not committed) the local transaction. Guarded so a rollback
                // failure never masks the original exception from the handler or the commit. The ambient
                // scope is deliberately NOT guarded: it commits on Dispose, so its failure must surface.
                if (localTransaction is not null)
                {
                    try
                    {
                        await localTransaction.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeException)
                    {
                        TransactionalOutboxLogMessages.TransactionDisposeFailed(
                            _logger, context.MessageId, disposeException);
                    }
                }

                scope?.Dispose();
            }
        }
        finally
        {
            // Stop exposing the pinned connection before it is closed — it must never leak to a
            // subsequent message processed on this asynchronous flow.
            _currentConnection.Value = null;
            _currentTransaction.Value = null;

            // Close the pinned connection on every path. Guard the close so a connection-close
            // failure never masks the original exception propagating from the try block (handler
            // fault or commit error) — the real cause must reach the transport for correct settlement.
            try
            {
                await _dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception closeException)
            {
                TransactionalOutboxLogMessages.ConnectionCloseFailed(_logger, context.MessageId, closeException);
            }
        }
    }
}

internal static partial class TransactionalOutboxLogMessages
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Flushing transactional outbox buffer for message {MessageId}: {MessageCount} message(s) to store")]
    internal static partial void FlushingBuffer(
        ILogger logger,
        Guid messageId,
        int messageCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Discarding transactional outbox buffer for message {MessageId}: {MessageCount} buffered message(s) lost due to handler exception")]
    internal static partial void DiscardingBuffer(
        ILogger logger,
        Guid messageId,
        int messageCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The retry-attempt callback registered by the transactional outbox for message {MessageId} was " +
                  "overwritten or removed by other middleware; messages from failed retry attempts may be duplicated")]
    internal static partial void RetryCallbackOverwritten(
        ILogger logger,
        Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Duplicate message {MessageId} skipped by transactional inbox filter")]
    internal static partial void DuplicateMessageSkipped(
        ILogger logger,
        Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Transactional outbox committed successfully for message {MessageId}")]
    internal static partial void TransactionCompleted(
        ILogger logger,
        Guid messageId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to dispose the local consume transaction for message {MessageId}; the original operation outcome is unaffected")]
    internal static partial void TransactionDisposeFailed(
        ILogger logger,
        Guid messageId,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to close the pinned database connection for message {MessageId}; the original operation outcome is unaffected")]
    internal static partial void ConnectionCloseFailed(
        ILogger logger,
        Guid messageId,
        Exception exception);
}
