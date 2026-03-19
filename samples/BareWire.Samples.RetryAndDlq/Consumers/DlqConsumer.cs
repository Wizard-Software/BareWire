using BareWire.Abstractions;
using BareWire.Samples.RetryAndDlq.Data;
using BareWire.Samples.RetryAndDlq.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.RetryAndDlq.Consumers;

/// <summary>
/// Consumes <see cref="ProcessPayment"/> messages that were routed to the dead-letter queue
/// <c>payments-dlq</c> after all retry attempts by <c>PaymentProcessor</c> were exhausted.
/// Persists a <see cref="FailedPayment"/> record to PostgreSQL for audit and investigation.
/// </summary>
/// <remarks>
/// Resolved from DI as transient per-message.
/// </remarks>
public sealed partial class DlqConsumer(
    ILogger<DlqConsumer> logger,
    PaymentDbContext dbContext) : IConsumer<ProcessPayment>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<ProcessPayment> context)
    {
        ProcessPayment payment = context.Message;

        LogDlqMessageReceived(logger, payment.PaymentId, payment.Amount, payment.Currency);

        FailedPayment entity = new()
        {
            PaymentId = payment.PaymentId,
            Amount = payment.Amount,
            Currency = payment.Currency,
            Error = "Payment declined after all retry attempts were exhausted.",
            FailedAt = DateTime.UtcNow,
        };

        dbContext.FailedPayments.Add(entity);
        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        LogFailedPaymentPersisted(logger, payment.PaymentId, entity.Id);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "DLQ: received dead-lettered payment PaymentId={PaymentId} Amount={Amount} {Currency}")]
    private static partial void LogDlqMessageReceived(
        ILogger logger, string paymentId, decimal amount, string currency);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "DLQ: persisted failed payment PaymentId={PaymentId} with record Id={RecordId}")]
    private static partial void LogFailedPaymentPersisted(
        ILogger logger, string paymentId, int recordId);
}
