namespace BareWire.Samples.RequestResponse.Data;

// EF Core entity — persisted to PostgreSQL when a ValidateOrder request is processed.
public sealed class ValidationRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string OrderId { get; init; }
    public bool IsValid { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset ValidatedAt { get; init; }
}
