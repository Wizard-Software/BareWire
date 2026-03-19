namespace BareWire.Samples.ObservabilityShowcase.Messages;

/// <summary>
/// Published when a demo order is created to initiate the observability pipeline.
/// </summary>
public sealed record DemoOrderCreated(string OrderId, decimal Amount, DateTime CreatedAt);
