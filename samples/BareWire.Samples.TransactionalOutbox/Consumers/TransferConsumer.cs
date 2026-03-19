using BareWire.Abstractions;
using BareWire.Samples.TransactionalOutbox.Data;
using BareWire.Samples.TransactionalOutbox.Messages;
using BareWire.Samples.TransactionalOutbox.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.TransactionalOutbox.Consumers;

/// <summary>
/// Processes <see cref="TransferInitiated"/> events and updates the corresponding
/// <see cref="Models.Transfer"/> record to "Completed" status.
/// </summary>
/// <remarks>
/// Inbox deduplication (exactly-once semantics) is handled transparently by
/// <c>TransactionalOutboxMiddleware</c> — this consumer does not need to perform any
/// duplicate-detection logic itself. If RabbitMQ redelivers the same message after a
/// crash, the middleware prevents the handler body from executing a second time.
/// </remarks>
public sealed partial class TransferConsumer(
    ILogger<TransferConsumer> logger,
    TransferDbContext dbContext) : IConsumer<TransferInitiated>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        TransferInitiated message = context.Message;

        LogTransferReceived(logger, message.TransferId, message.FromAccount, message.ToAccount, message.Amount);

        // Simulate a short processing delay (e.g. calling a payment gateway).
        await Task.Delay(millisecondsDelay: 50, context.CancellationToken).ConfigureAwait(false);

        // Update the transfer status in the same transaction managed by TransactionalOutboxMiddleware.
        // The OutboxDbContext and TransferDbContext share the same connection string,
        // so both participate in the same ambient TransactionScope.
        Transfer? transfer = await dbContext.Transfers
            .FirstOrDefaultAsync(t => t.TransferId == message.TransferId, context.CancellationToken)
            .ConfigureAwait(false);

        if (transfer is not null)
        {
            transfer.Status = "Completed";
            LogTransferCompleted(logger, message.TransferId);
        }
        else
        {
            // Guard: the Transfer row was not found (e.g. consumer is running against a different DB).
            // Record a processing entry so the inbox deduplication record is still committed.
            LogTransferNotFound(logger, message.TransferId);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Received TransferInitiated: {TransferId} — {FromAccount} → {ToAccount} for {Amount:C}")]
    private static partial void LogTransferReceived(
        ILogger logger, string transferId, string fromAccount, string toAccount, decimal amount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Transfer {TransferId} marked as Completed")]
    private static partial void LogTransferCompleted(ILogger logger, string transferId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Transfer {TransferId} not found in DB — may be running against a different database")]
    private static partial void LogTransferNotFound(ILogger logger, string transferId);
}
