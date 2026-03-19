namespace BareWire.Samples.BasicPublishConsume.Messages;

// Past-tense event name per ADR-005 naming conventions (CONSTITUTION.md).
// Plain record — no base class, no attributes (ADR-001 raw-first).
public record MessageSent(string Content, DateTime SentAt);
