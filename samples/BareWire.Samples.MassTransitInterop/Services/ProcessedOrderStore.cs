using System.Collections.Concurrent;

namespace BareWire.Samples.MassTransitInterop.Services;

public record ProcessedOrder(
    string OrderId,
    decimal Amount,
    string Currency,
    string Source,
    DateTimeOffset ProcessedAt);

internal sealed class ProcessedOrderStore
{
    private readonly ConcurrentBag<ProcessedOrder> _orders = [];

    public void Add(ProcessedOrder order) => _orders.Add(order);

    public IReadOnlyList<ProcessedOrder> GetAll() => [.. _orders];
}
