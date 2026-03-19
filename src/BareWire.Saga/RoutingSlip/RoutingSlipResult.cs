namespace BareWire.Saga.RoutingSlip;

internal abstract record RoutingSlipResult;

internal sealed record RoutingSlipCompleted(IReadOnlyList<object> Logs) : RoutingSlipResult;

internal sealed record RoutingSlipFaulted(
    Exception Exception,
    int FailedAtStep,
    bool CompensationSucceeded) : RoutingSlipResult;
