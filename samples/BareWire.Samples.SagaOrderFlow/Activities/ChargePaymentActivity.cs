using BareWire.Abstractions.Saga;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.SagaOrderFlow.Activities;

/// <summary>Arguments for <see cref="ChargePaymentActivity"/>.</summary>
public sealed record ChargePaymentArguments(string OrderId, decimal Amount);

/// <summary>Compensation log produced by <see cref="ChargePaymentActivity.ExecuteAsync"/>.</summary>
public sealed record ChargePaymentLog(string OrderId, string ChargeId);

/// <summary>
/// Compensable activity that charges the customer for an order.
/// On compensation, issues a refund for the original charge.
/// </summary>
public sealed partial class ChargePaymentActivity(ILogger<ChargePaymentActivity> logger)
    : ICompensableActivity<ChargePaymentArguments, ChargePaymentLog>
{
    /// <inheritdoc />
    public Task<ChargePaymentLog> ExecuteAsync(
        ChargePaymentArguments arguments,
        CancellationToken cancellationToken = default)
    {
        string chargeId = Guid.NewGuid().ToString();
        LogPaymentCharged(arguments.OrderId, chargeId, arguments.Amount);
        return Task.FromResult(new ChargePaymentLog(arguments.OrderId, chargeId));
    }

    /// <inheritdoc />
    public Task CompensateAsync(
        ChargePaymentLog log,
        CancellationToken cancellationToken = default)
    {
        LogPaymentRefunded(log.OrderId, log.ChargeId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Payment charged for order {OrderId}: charge {ChargeId}, amount {Amount}")]
    private partial void LogPaymentCharged(string orderId, string chargeId, decimal amount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Payment {ChargeId} refunded for order {OrderId} (compensation)")]
    private partial void LogPaymentRefunded(string orderId, string chargeId);
}
