namespace BareWire.Samples.TransactionalOutbox.Models;

/// <summary>
/// Represents a fund transfer entity persisted to PostgreSQL.
/// </summary>
public sealed class Transfer
{
    public int Id { get; set; }
    public string TransferId { get; set; } = null!;
    public string FromAccount { get; set; } = null!;
    public string ToAccount { get; set; } = null!;
    public decimal Amount { get; set; }

    /// <summary>
    /// Lifecycle status: "Pending", "Processing", or "Completed".
    /// </summary>
    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
