namespace BareWire.Samples.TransactionalOutbox.Messages;

/// <summary>
/// Event published when a fund transfer has been initiated.
/// Consumed by <see cref="BareWire.Samples.TransactionalOutbox.Consumers.TransferConsumer"/>.
/// </summary>
public sealed record TransferInitiated(
    string TransferId,
    string FromAccount,
    string ToAccount,
    decimal Amount,
    DateTime InitiatedAt);
