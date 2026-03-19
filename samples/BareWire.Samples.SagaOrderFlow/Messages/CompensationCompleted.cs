namespace BareWire.Samples.SagaOrderFlow.Messages;

/// <summary>Raised when all compensation activities for a failed order have completed.</summary>
public sealed record CompensationCompleted(string OrderId);
