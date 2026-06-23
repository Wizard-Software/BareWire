namespace BareWire.Outbox.EntityFramework;

internal sealed class OutboxMessage
{
    public long Id { get; set; }

    public Guid MessageId { get; set; }

    public string DestinationAddress { get; set; } = null!;

    public string ContentType { get; set; } = null!;

    public byte[] Payload { get; set; } = null!;

    public string? Headers { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public int RetryCount { get; set; }

    /// <summary>
    /// UTC timestamp when this row was claimed by an outbox dispatcher instance.
    /// <see langword="null"/> means the row is unclaimed and available for dispatch.
    /// </summary>
    public DateTimeOffset? LockedAt { get; set; }

    /// <summary>
    /// Identifier of the outbox dispatcher instance that claimed this row.
    /// <see langword="null"/> when the row is unclaimed.
    /// Maximum length 256 characters.
    /// </summary>
    public string? LockedBy { get; set; }
}
