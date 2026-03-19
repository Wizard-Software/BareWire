namespace BareWire.Samples.RabbitMQ.Models;

/// <summary>Request body for <c>POST /orders</c>.</summary>
/// <param name="Amount">The order total in the specified currency.</param>
/// <param name="Currency">ISO-4217 currency code (e.g. <c>"USD"</c>).</param>
public sealed record OrderRequest(decimal Amount, string Currency);
