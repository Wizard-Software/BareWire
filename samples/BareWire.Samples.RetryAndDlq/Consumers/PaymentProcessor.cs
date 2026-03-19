using BareWire.Abstractions;
using BareWire.Samples.RetryAndDlq.Exceptions;
using BareWire.Samples.RetryAndDlq.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.RetryAndDlq.Consumers;

/// <summary>
/// Processes <see cref="ProcessPayment"/> commands, simulating a 70% decline rate to
/// demonstrate BareWire retry behaviour followed by dead-letter queue routing.
/// </summary>
/// <remarks>
/// Resolved from DI as transient per-message. The consumer is stateless — all randomness
/// comes from <see cref="Random.Shared"/>, which is thread-safe.
/// </remarks>
public sealed partial class PaymentProcessor(
    ILogger<PaymentProcessor> logger) : IConsumer<ProcessPayment>
{
    private const double FailureRate = 0.7;

    /// <inheritdoc />
    public Task ConsumeAsync(ConsumeContext<ProcessPayment> context)
    {
        ProcessPayment payment = context.Message;

        if (Random.Shared.NextDouble() < FailureRate)
        {
            LogPaymentDeclined(logger, payment.PaymentId, payment.Amount, payment.Currency);
            throw new PaymentDeclinedException(payment.PaymentId);
        }

        LogPaymentProcessed(logger, payment.PaymentId, payment.Amount, payment.Currency);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Payment processed successfully: PaymentId={PaymentId} Amount={Amount} {Currency}")]
    private static partial void LogPaymentProcessed(
        ILogger logger, string paymentId, decimal amount, string currency);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Payment declined (will retry): PaymentId={PaymentId} Amount={Amount} {Currency}")]
    private static partial void LogPaymentDeclined(
        ILogger logger, string paymentId, decimal amount, string currency);
}
