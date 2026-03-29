namespace BareWire.Samples.MassTransitInterop.Messages;

public record OrderCreated(string OrderId, decimal Amount, string Currency);
