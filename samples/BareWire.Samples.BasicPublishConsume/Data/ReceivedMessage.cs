namespace BareWire.Samples.BasicPublishConsume.Data;

// EF Core entity — persisted to PostgreSQL when a MessageSent event is consumed.
public sealed class ReceivedMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Content { get; init; }
    public DateTime ReceivedAt { get; init; }
}
