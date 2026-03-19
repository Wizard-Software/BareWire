namespace BareWire.Samples.BackpressureDemo.Messages;

/// <summary>
/// A load-test payload published at high rates to demonstrate ADR-004 and ADR-006 backpressure.
/// </summary>
public sealed record LoadTestMessage(int SequenceNumber, DateTime CreatedAt);
