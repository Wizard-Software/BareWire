using BareWire.Abstractions;

using Microsoft.Extensions.Logging;

namespace BareWire.Samples.InMemoryModularMonolith.Modules.Ordering;

/// <summary>
/// The Ordering module's single entry point: places an order and publishes <see cref="OrderPlaced"/>
/// to the fanout ordering exchange. Registered as a singleton — see the "OrderingModule lifetime"
/// remark on <c>Program.cs</c> for why it must not be scoped.
/// </summary>
/// <param name="publisher">Publishes <see cref="OrderPlaced"/> to the bus.</param>
/// <param name="clock">Supplies the order's placed-at timestamp.</param>
/// <param name="logger">Logs the placed order.</param>
public sealed partial class OrderingModule(
    IPublishEndpoint publisher, TimeProvider clock, ILogger<OrderingModule> logger)
{
    /// <summary>
    /// Places an order for <paramref name="customerId"/> and publishes <see cref="OrderPlaced"/>.
    /// </summary>
    /// <param name="customerId">The customer placing the order. Must already be validated by the caller.</param>
    /// <param name="amount">The order amount. Must already be validated by the caller.</param>
    /// <param name="cancellationToken">A token to cancel the publish.</param>
    /// <returns>The generated order identifier.</returns>
    public async Task<string> PlaceOrderAsync(
        string customerId, decimal amount, CancellationToken cancellationToken = default)
    {
        string orderId = Guid.NewGuid().ToString("N");
        var placed = new OrderPlaced(orderId, customerId, amount, clock.GetUtcNow());

        await publisher.PublishAsync(placed, cancellationToken).ConfigureAwait(false);

        LogOrderPlaced(logger, orderId, customerId);
        return orderId;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ordering placed order {OrderId} for customer {CustomerId}")]
    private static partial void LogOrderPlaced(ILogger logger, string orderId, string customerId);
}
