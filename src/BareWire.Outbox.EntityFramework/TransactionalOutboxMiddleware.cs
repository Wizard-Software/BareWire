using System.Data.Common;
using System.Transactions;
using BareWire.Abstractions.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using static BareWire.Abstractions.Pipeline.WellKnownItemKeys;

namespace BareWire.Outbox.EntityFramework;

internal sealed partial class TransactionalOutboxMiddleware : IMessageMiddleware
{
    // Header key written by RabbitMqHeaderMapper for the message type discriminator.
    private const string MessageTypeHeader = "BW-MessageType";

    private static readonly AsyncLocal<OutboxBuffer?> _current = new();

    // The physical connection pinned for the in-flight consume operation, flowed across the
    // consumer's (separate) DI scope via the async execution context. A consumer DbContext can
    // share this exact connection so its business write commits single-phase with the outbox
    // write and the inbox marker — no second connection, no escalation to a two-phase commit.
    private static readonly AsyncLocal<DbConnection?> _currentConnection = new();

    private readonly OutboxDbContext _dbContext;
    private readonly IOutboxStore _outboxStore;
    private readonly InboxFilter _inboxFilter;
    private readonly ILogger<TransactionalOutboxMiddleware> _logger;

    internal static OutboxBuffer? Current => _current.Value;

    internal static DbConnection? CurrentConnection => _currentConnection.Value;

    internal TransactionalOutboxMiddleware(
        OutboxDbContext dbContext,
        IOutboxStore outboxStore,
        InboxFilter inboxFilter,
        ILogger<TransactionalOutboxMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(outboxStore);
        ArgumentNullException.ThrowIfNull(inboxFilter);
        ArgumentNullException.ThrowIfNull(logger);

        _dbContext = dbContext;
        _outboxStore = outboxStore;
        _inboxFilter = inboxFilter;
        _logger = logger;
    }

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
        // (ExecuteUpdateAsync) share ONE physical connection enlisted once in the ambient
        // TransactionScope — preventing DTC escalation on Npgsql / non-Windows hosts.
        await _dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Publish the pinned connection on the async flow so a consumer DbContext (resolved in its
        // own DI scope, but on the same execution context) can share this exact connection. Sharing
        // one connection lets the business write commit single-phase with the outbox messages and the
        // inbox marker — no second connection, hence no escalation to a two-phase (prepared) commit.
        _currentConnection.Value = _dbContext.Database.GetDbConnection();
        try
        {
            // 1. Inbox deduplication check — deliberately OUTSIDE the TransactionScope so the
            //    lock row is committed immediately and visible to other workers even if the
            //    business transaction later rolls back.
            bool lockAcquired = await _inboxFilter
                .TryLockAsync(context.MessageId, consumerType, ct)
                .ConfigureAwait(false);

            if (!lockAcquired)
            {
                TransactionalOutboxLogMessages.DuplicateMessageSkipped(_logger, context.MessageId);
                context.Items[InboxFiltered] = true;
                return;
            }

            var buffer = new OutboxBuffer();
            _current.Value = buffer;

            // 2. Begin ambient transaction. The connection is already open and will be enlisted
            //    once in this scope. Both SaveChangesAsync and MarkProcessedAsync use the same
            //    enlisted connection — no second connection is opened, DTC is never triggered.
            using var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                TransactionScopeAsyncFlowOption.Enabled);

            try
            {
                // 3. Invoke handler — business logic runs inside the transaction.
                await nextMiddleware(context).ConfigureAwait(false);

                // 4. Flush outbox buffer — add OutboxMessage entities to DbContext (no SaveChanges yet).
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
                scope.Complete();

                TransactionalOutboxLogMessages.TransactionCompleted(_logger, context.MessageId);
            }
            catch
            {
                // Buffer is discarded; DbContext changes are not saved; TransactionScope
                // disposes without Complete() — automatic rollback of all three writes.
                int discardCount = buffer.GetMessages().Count;
                TransactionalOutboxLogMessages.DiscardingBuffer(_logger, context.MessageId, discardCount);
                buffer.Clear();
                throw;
            }
            finally
            {
                _current.Value = null;
            }
        }
        finally
        {
            // Stop exposing the pinned connection before it is closed — it must never leak to a
            // subsequent message processed on this asynchronous flow.
            _currentConnection.Value = null;

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
        Message = "Failed to close the pinned database connection for message {MessageId}; the original operation outcome is unaffected")]
    internal static partial void ConnectionCloseFailed(
        ILogger logger,
        Guid messageId,
        Exception exception);
}
