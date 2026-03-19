namespace BareWire.Samples.RequestResponse.Messages;

// Command message — imperative naming convention (CONSTITUTION.md).
// Plain record with no base class or attributes (ADR-001: raw-first, no envelope).
public record ValidateOrder(string OrderId, decimal Amount);
