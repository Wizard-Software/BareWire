namespace BareWire.Samples.RequestResponse.Messages;

// Response message — descriptive naming convention (CONSTITUTION.md).
// Plain record with no base class or attributes (ADR-001: raw-first, no envelope).
public record OrderValidationResult(string OrderId, bool IsValid, string? Reason);
